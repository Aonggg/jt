//go:build windows

// jt stores secrets AES-GCM encrypted in a Git-synced vault and hands them to
// programs as jt://secret/<id> references, so an AI agent can use a credential
// without the plaintext ever entering its context. This build is Windows-only:
// the master key is DPAPI-protected and the clipboard is the capture channel.
package main

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"slices"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	version = "0.5.3"
	prefix  = "jt://secret/"
	// groupPrefix names a whole namespace: jt://env/cf stands for every cf/* entry.
	groupPrefix = "jt://env/"
)

type config struct {
	Vault string `json:"vault"`
	Key   string `json:"key"`
}
type entry struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	Description string `json:"description,omitempty"`
	Ciphertext  string `json:"ciphertext"`
	Preview     string `json:"preview"`
	CreatedAt   string `json:"created_at,omitempty"`
	UpdatedAt   string `json:"updated_at,omitempty"`
}
type vault struct {
	Version int     `json:"version"`
	Secrets []entry `json:"secrets"`
}

// stdout is where list/status write their output; tests swap it.
var stdout io.Writer = os.Stdout

var tokenPattern = regexp.MustCompile("^jt://secret/[0-9A-Za-z]{8}$")
var envPattern = regexp.MustCompile("^[A-Za-z_][A-Za-z0-9_]*$")
var idPattern = regexp.MustCompile("^[0-9A-Za-z]{8}$")

func main() {
	if len(os.Args) < 2 {
		usage()
		return
	}
	var err error
	switch os.Args[1] {
	case "add":
		err = add(os.Args[2:])
	case "grab":
		err = grab(os.Args[2:])
	case "ref":
		err = ref(os.Args[2:])
	case "copy":
		err = copyValue(os.Args[2:])
	case "key":
		err = keyCommand(os.Args[2:])
	case "ls", "list":
		err = list(os.Args[2:])
	case "set":
		err = set(os.Args[2:])
	case "describe":
		err = describe(os.Args[2:])
	case "rm", "remove":
		err = remove(os.Args[2:])
	case "mv", "rename":
		err = rename(os.Args[2:])
	case "resolve":
		err = resolve(os.Args[2:])
	case "env":
		err = env(os.Args[2:])
	case "init":
		err = initVault(os.Args[2:])
	case "sync":
		err = syncVault(os.Args[2:])
	case "status":
		err = status(os.Args[2:])
	case "version":
		fmt.Println("jt " + version)
	case "help", "--help", "-h":
		usage()
	default:
		err = fmt.Errorf("unknown command %q", os.Args[1])
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "jt:", err)
		if e, ok := err.(exitStatusError); ok {
			os.Exit(e.code)
		}
		os.Exit(1)
	}
}
func usage() {
	fmt.Print(`jt - encrypted secret references for Windows

Usage:
  jt init [--repo URL] [--vault DIR] [--key FILE]
  jt grab <name> [--id ID] [--description TEXT] [--clear-history]
  jt add <name> [--from-clipboard] [--id ID] [--description TEXT]
  jt ls [--json] [query]
  jt set <name-or-ref> [--from-clipboard] [--description TEXT]
  jt describe <name-or-ref> <description>
  jt rm <name-or-ref> | <namespace>/
  jt mv <name-or-ref> <new-name> | <namespace>/ <new-namespace>/
  jt ref <name-or-ref|namespace>
  jt copy <name-or-ref>
  jt resolve <name-or-ref> [--env NAME] --exec COMMAND [ARGS...]
  jt env <namespace|jt://env/namespace> -- COMMAND [ARGS...]
  jt key export | import [KEY]
  jt sync
  jt status [--json]

grab encrypts the clipboard text and replaces it with a jt://secret/<id> reference.
add/set read the value from stdin unless --from-clipboard is used.
ref copies a reference to the clipboard; given a namespace it copies the whole
group (jt://env/<namespace> plus every name and reference). copy puts the
plaintext there for you, excluded from Windows clipboard history.
resolve and env decrypt only into the environment of the child process and
never print values; the command runs directly, name a shell (cmd /C, bash -c)
when you need one. resolve uses JT_SECRET unless --env sets another name.
key export puts the base64 master key on the clipboard; key import reads it.
describe edits only metadata; pass an empty string to clear the description.
Descriptions are plaintext, synced metadata; never put secrets in them.
The first nonempty description upgrades the vault to v2; upgrade all clients first.
ls --json prints id, ref, name, description, preview and timestamps; never ciphertext.
status reports local vault state (uncommitted changes, commits not pushed) without network access.
`)
}

// paths resolves config, vault and key locations. Everything defaults to
// %LOCALAPPDATA%\jt: machine-local, so a roaming profile never carries the key.
func paths() (string, string, string) {
	base := os.Getenv("JT_HOME")
	if base == "" {
		local := os.Getenv("LOCALAPPDATA")
		if local == "" {
			home, err := os.UserHomeDir()
			if err != nil {
				home = "."
			}
			local = filepath.Join(home, "AppData", "Local")
		}
		base = filepath.Join(local, "jt")
	}
	vaultDir := os.Getenv("JT_VAULT_DIR")
	if vaultDir == "" {
		vaultDir = filepath.Join(base, "vault")
	}
	keyPath := os.Getenv("JT_KEY_FILE")
	if keyPath == "" {
		keyPath = filepath.Join(base, "key")
	}
	return filepath.Join(base, "config.json"), vaultDir, keyPath
}
func loadConfig() (config, error) {
	configPath, vaultDir, keyPath := paths()
	c := config{Vault: vaultDir, Key: keyPath}
	data, err := os.ReadFile(configPath)
	if errors.Is(err, os.ErrNotExist) {
		return c, nil
	}
	if err != nil {
		return c, err
	}
	if err := json.Unmarshal(data, &c); err != nil {
		return c, fmt.Errorf("read config: %w", err)
	}
	return c, nil
}
func saveConfig(c config) error {
	configPath, _, _ := paths()
	if err := os.MkdirAll(filepath.Dir(configPath), 0700); err != nil {
		return err
	}
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return err
	}
	return atomicWrite(configPath, data)
}
func openVault(c config, create bool) (vault, []byte, error) {
	key, err := loadKey(c.Key, create)
	if err != nil {
		return vault{}, nil, fmt.Errorf("load key: %w", err)
	}
	path := filepath.Join(c.Vault, "vault.json")
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) && create {
		return vault{Version: 1, Secrets: []entry{}}, key, nil
	}
	if err != nil {
		return vault{}, nil, fmt.Errorf("read vault: %w", err)
	}
	var v vault
	if err := json.Unmarshal(data, &v); err != nil {
		return v, nil, fmt.Errorf("read vault: %w", err)
	}
	if v.Version != 1 && v.Version != 2 {
		return v, nil, fmt.Errorf("unsupported vault version %d", v.Version)
	}
	return v, key, nil
}
func saveVault(c config, v vault) error {
	if v.Version != 1 && v.Version != 2 {
		return fmt.Errorf("unsupported vault version %d", v.Version)
	}
	// Older clients reject v2 instead of silently dropping unknown entry fields.
	// Never downgrade after descriptions have been cleared or entries removed.
	for _, item := range v.Secrets {
		if item.Description != "" {
			v.Version = 2
			break
		}
	}
	if err := os.MkdirAll(c.Vault, 0700); err != nil {
		return err
	}
	// Sort a copy: callers may still hold a pointer to an entry for their result.
	secrets := make([]entry, len(v.Secrets))
	copy(secrets, v.Secrets)
	v.Secrets = secrets
	sort.Slice(v.Secrets, func(i, j int) bool { return v.Secrets[i].Name < v.Secrets[j].Name })
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	return atomicWrite(filepath.Join(c.Vault, "vault.json"), data)
}

// atomicWrite replaces path via a temp file in the same directory. The files
// live under the user's profile, which Windows already restricts to that user.
func atomicWrite(path string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(filepath.Dir(path), ".jt-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	defer os.Remove(tmpName)
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(tmpName, path)
}
func cipherFor(key []byte) (cipher.AEAD, error) {
	b, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	return cipher.NewGCM(b)
}
func seal(key []byte, value string) (string, error) {
	aead, err := cipherFor(key)
	if err != nil {
		return "", err
	}
	nonce := make([]byte, aead.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return "", err
	}
	combined := append(nonce, aead.Seal(nil, nonce, []byte(value), nil)...)
	return base64.StdEncoding.EncodeToString(combined), nil
}
func open(key []byte, encoded string) (string, error) {
	aead, err := cipherFor(key)
	if err != nil {
		return "", err
	}
	combined, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil || len(combined) < aead.NonceSize() {
		return "", errors.New("invalid ciphertext")
	}
	nonce, ciphertext := combined[:aead.NonceSize()], combined[aead.NonceSize():]
	plain, err := aead.Open(nil, nonce, ciphertext, nil)
	if err != nil {
		return "", errors.New("ciphertext authentication failed")
	}
	return string(plain), nil
}
func preview(value string) string {
	r := []rune(value)
	if len(r) <= 8 {
		return strings.Repeat("*", len(r))
	}
	return string(r[:2]) + strings.Repeat("*", len(r)-6) + string(r[len(r)-4:])
}
func newID(existing map[string]bool) (string, error) {
	const alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
	buf := make([]byte, 8)
	for {
		if _, err := io.ReadFull(rand.Reader, buf); err != nil {
			return "", err
		}
		id := make([]byte, 8)
		for i, b := range buf {
			id[i] = alphabet[int(b)%len(alphabet)]
		}
		if !existing[string(id)] {
			return string(id), nil
		}
	}
}

// readValue takes the secret from the clipboard or stdin. One trailing line
// break is dropped: echo, PowerShell pipes and copied terminal lines add one.
func readValue(fromClipboard bool) (string, error) {
	if fromClipboard {
		text, err := readClipboard()
		if err != nil {
			return "", err
		}
		return trimLineEnd(text), nil
	}
	data, err := io.ReadAll(os.Stdin)
	if err != nil {
		return "", err
	}
	return trimLineEnd(string(data)), nil
}
func trimLineEnd(s string) string {
	return strings.TrimSuffix(strings.TrimSuffix(s, "\n"), "\r")
}

type writeOptions struct {
	fromClipboard bool
	clearHistory  bool
	id            string
	description   *string // nil means leave the existing description unchanged.
	positional    []string
}

// parseWriteFlags recognises the boolean flags --from-clipboard and
// --clear-history and the valued flags --id and --description, limited to the
// ones listed in allowed. Flags may precede or follow positional arguments.
// Use -- to end flags, or --description=TEXT for a description beginning with
// a dash.
func parseWriteFlags(args []string, allowed ...string) (writeOptions, error) {
	var opts writeOptions
	seen := map[string]bool{}
	for i := 0; i < len(args); i++ {
		arg := args[i]
		if arg == "--" {
			opts.positional = append(opts.positional, args[i+1:]...)
			break
		}
		if !strings.HasPrefix(arg, "-") || arg == "-" {
			opts.positional = append(opts.positional, arg)
			continue
		}
		name, value, hasValue := strings.Cut(arg, "=")
		if !slices.Contains(allowed, name) {
			return opts, fmt.Errorf("unknown option %q", name)
		}
		if seen[name] {
			return opts, fmt.Errorf("duplicate option %s", name)
		}
		seen[name] = true
		if name == "--from-clipboard" || name == "--clear-history" {
			if hasValue {
				return opts, fmt.Errorf("%s does not take a value", name)
			}
			if name == "--from-clipboard" {
				opts.fromClipboard = true
			} else {
				opts.clearHistory = true
			}
			continue
		}
		if !hasValue {
			if i+1 >= len(args) || strings.HasPrefix(args[i+1], "-") {
				return opts, fmt.Errorf("%s needs a value (use %s=TEXT for a value beginning with a dash)", name, name)
			}
			i++
			value = args[i]
		}
		switch name {
		case "--id":
			if !idPattern.MatchString(value) {
				return opts, errors.New("id must be 8 base62 characters")
			}
			opts.id = value
		case "--description":
			if !utf8.ValidString(value) {
				return opts, errors.New("description must be valid UTF-8")
			}
			opts.description = &value
		}
	}
	return opts, nil
}
func add(args []string) error {
	opts, err := parseWriteFlags(args, "--from-clipboard", "--id", "--description")
	if err != nil {
		return err
	}
	positional := opts.positional
	if len(positional) != 1 || positional[0] == "" {
		return errors.New("add needs <name>")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, key, err := openVault(c, true)
	if err != nil {
		return err
	}
	value, err := readValue(opts.fromClipboard)
	if err != nil {
		return err
	}
	item, err := addEntry(c, v, key, positional[0], value, opts)
	if err != nil {
		return err
	}
	fmt.Printf("added %s %s\n", item.Name, prefix+item.ID)
	return nil
}

// addEntry encrypts value as a new entry named name and saves the vault.
func addEntry(c config, v vault, key []byte, name, value string, opts writeOptions) (entry, error) {
	if value == "" {
		return entry{}, errors.New("refusing to add an empty value")
	}
	for _, item := range v.Secrets {
		if item.Name == name {
			return entry{}, fmt.Errorf("name already exists: %s", name)
		}
	}
	id := opts.id
	if id == "" {
		ids := map[string]bool{}
		for _, item := range v.Secrets {
			ids[item.ID] = true
		}
		var err error
		if id, err = newID(ids); err != nil {
			return entry{}, err
		}
	}
	if !idPattern.MatchString(id) {
		return entry{}, errors.New("id must be 8 base62 characters")
	}
	for _, item := range v.Secrets {
		if item.ID == id {
			return entry{}, fmt.Errorf("id already exists: %s", id)
		}
	}
	ciphertext, err := seal(key, value)
	if err != nil {
		return entry{}, err
	}
	now := time.Now().UTC().Format(time.RFC3339)
	item := entry{ID: id, Name: name, Ciphertext: ciphertext, Preview: preview(value), CreatedAt: now, UpdatedAt: now}
	if opts.description != nil {
		item.Description = *opts.description
	}
	v.Secrets = append(v.Secrets, item)
	if err := saveVault(c, v); err != nil {
		return entry{}, err
	}
	return item, nil
}

type listItem struct {
	ID          string  `json:"id"`
	Ref         string  `json:"ref"`
	Name        string  `json:"name"`
	Description string  `json:"description"`
	Preview     string  `json:"preview"`
	CreatedAt   *string `json:"created_at"`
	UpdatedAt   *string `json:"updated_at"`
}

func list(args []string) error {
	asJSON := false
	queries := []string{}
	for _, arg := range args {
		if arg == "--json" {
			asJSON = true
		} else {
			queries = append(queries, arg)
		}
	}
	if len(queries) > 1 {
		return errors.New("ls accepts at most one query")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, _, err := openVault(c, false)
	if err != nil {
		return err
	}
	query := ""
	if len(queries) == 1 {
		query = queries[0]
	}
	items := filterEntries(v.Secrets, query)
	if asJSON {
		out := make([]listItem, 0, len(items))
		for _, item := range items {
			out = append(out, listItem{ID: item.ID, Ref: prefix + item.ID, Name: item.Name, Description: item.Description, Preview: item.Preview, CreatedAt: optional(item.CreatedAt), UpdatedAt: optional(item.UpdatedAt)})
		}
		data, err := json.MarshalIndent(out, "", "  ")
		if err != nil {
			return err
		}
		_, err = fmt.Fprintf(stdout, "%s\n", data)
		return err
	}
	for _, item := range items {
		if item.Description == "" {
			fmt.Fprintf(stdout, "%s | %s | %s\n", item.Name, prefix+item.ID, item.Preview)
		} else {
			// Quote metadata so newlines and terminal controls cannot forge list rows.
			fmt.Fprintf(stdout, "%s | %s | %s | %s\n", item.Name, prefix+item.ID, item.Preview, strconv.QuoteToGraphic(item.Description))
		}
	}
	return nil
}
func filterEntries(secrets []entry, query string) []entry {
	lower := strings.ToLower(query)
	items := []entry{}
	for _, item := range secrets {
		if query != "" && !strings.Contains(strings.ToLower(item.Name), lower) && !strings.Contains(strings.ToLower(item.Description), lower) && !strings.Contains(item.ID, query) {
			continue
		}
		items = append(items, item)
	}
	sort.SliceStable(items, func(i, j int) bool {
		if items[i].Name != items[j].Name {
			return items[i].Name < items[j].Name
		}
		return items[i].ID < items[j].ID
	})
	return items
}
func optional(value string) *string {
	if value == "" {
		return nil
	}
	return &value
}
func find(v vault, ref string) (int, *entry, error) {
	ref = strings.TrimSpace(ref)
	if strings.HasPrefix(ref, prefix) && !tokenPattern.MatchString(ref) {
		return -1, nil, errors.New("invalid secret reference")
	}
	for i := range v.Secrets {
		if prefix+v.Secrets[i].ID == ref || v.Secrets[i].Name == ref {
			return i, &v.Secrets[i], nil
		}
	}
	return -1, nil, errors.New("secret not found")
}
func set(args []string) error {
	opts, err := parseWriteFlags(args, "--from-clipboard", "--description")
	if err != nil {
		return err
	}
	positional := opts.positional
	if len(positional) != 1 || positional[0] == "" {
		return errors.New("set needs <name-or-ref>")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, key, err := openVault(c, false)
	if err != nil {
		return err
	}
	index, item, err := find(v, positional[0])
	if err != nil {
		return err
	}
	value, err := readValue(opts.fromClipboard)
	if err != nil {
		return err
	}
	if value == "" {
		return errors.New("refusing to set an empty value")
	}
	ciphertext, err := seal(key, value)
	if err != nil {
		return err
	}
	if opts.description != nil {
		item.Description = *opts.description
	}
	item.Ciphertext = ciphertext
	item.Preview = preview(value)
	item.UpdatedAt = time.Now().UTC().Format(time.RFC3339)
	v.Secrets[index] = *item
	if err := saveVault(c, v); err != nil {
		return err
	}
	fmt.Printf("updated %s %s\n", item.Name, prefix+item.ID)
	return nil
}

// describe never reads stdin or decrypts/re-encrypts the value. The ID,
// ciphertext, preview and creation time remain unchanged.
func describe(args []string) error {
	if len(args) != 2 || args[0] == "" {
		return errors.New("describe needs <name-or-ref> <description>; use an empty string to clear")
	}
	if !utf8.ValidString(args[1]) {
		return errors.New("description must be valid UTF-8")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, _, err := openVault(c, false)
	if err != nil {
		return err
	}
	_, item, err := find(v, args[0])
	if err != nil {
		return err
	}
	if item.Description != args[1] {
		item.Description = args[1]
		item.UpdatedAt = time.Now().UTC().Format(time.RFC3339)
		if err := saveVault(c, v); err != nil {
			return err
		}
	}
	fmt.Printf("described %s %s\n", item.Name, prefix+item.ID)
	return nil
}

// remove deletes one entry, or with `<namespace>/` (or jt://env/<namespace>)
// every entry of a group; the trailing slash makes the mass deletion explicit.
func remove(args []string) error {
	if len(args) != 1 {
		return errors.New("rm needs <name-or-ref> or <namespace>/")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, _, err := openVault(c, false)
	if err != nil {
		return err
	}
	if namespace, _, ok := groupArgs(args[0], "x/"); ok {
		members := groupMembers(v, namespace)
		if len(members) == 0 {
			return fmt.Errorf("no entries under %q", namespace+"/")
		}
		kept := make([]entry, 0, len(v.Secrets)-len(members))
		for _, item := range v.Secrets {
			if !strings.HasPrefix(item.Name, namespace+"/") {
				kept = append(kept, item)
			}
		}
		v.Secrets = kept
		if err := saveVault(c, v); err != nil {
			return err
		}
		fmt.Printf("removed group %s/ (%d entries)\n", namespace, len(members))
		return nil
	}
	index, item, err := find(v, args[0])
	if err != nil {
		return err
	}
	removed := *item
	v.Secrets = append(v.Secrets[:index], v.Secrets[index+1:]...)
	if err := saveVault(c, v); err != nil {
		return err
	}
	fmt.Printf("removed %s %s\n", removed.Name, prefix+removed.ID)
	return nil
}

// rename changes one entry's name, or with `old/ new/` (trailing slashes, or
// jt://env/ tokens) moves every entry of a group to a new namespace. IDs and
// references never change; only jt://env/<namespace> does.
func rename(args []string) error {
	if len(args) != 2 || args[1] == "" {
		return errors.New("mv needs <name-or-ref> <new-name>, or <namespace>/ <new-namespace>/")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, _, err := openVault(c, false)
	if err != nil {
		return err
	}
	if from, to, ok := groupArgs(args[0], args[1]); ok {
		members := groupMembers(v, from)
		if len(members) == 0 {
			return fmt.Errorf("no entries under %q", from+"/")
		}
		names := map[string]bool{}
		for _, item := range v.Secrets {
			names[item.Name] = true
		}
		for _, i := range members {
			target := to + "/" + strings.TrimPrefix(v.Secrets[i].Name, from+"/")
			if names[target] {
				return fmt.Errorf("name already exists: %s", target)
			}
		}
		now := time.Now().UTC().Format(time.RFC3339)
		for _, i := range members {
			v.Secrets[i].Name = to + "/" + strings.TrimPrefix(v.Secrets[i].Name, from+"/")
			v.Secrets[i].UpdatedAt = now
		}
		if err := saveVault(c, v); err != nil {
			return err
		}
		fmt.Printf("renamed group %s/ to %s/ (%d entries; references unchanged)\n", from, to, len(members))
		return nil
	}
	_, item, err := find(v, args[0])
	if err != nil {
		return err
	}
	for _, other := range v.Secrets {
		if other.Name == args[1] {
			return fmt.Errorf("name already exists: %s", args[1])
		}
	}
	item.Name = args[1]
	item.UpdatedAt = time.Now().UTC().Format(time.RFC3339)
	if err := saveVault(c, v); err != nil {
		return err
	}
	fmt.Printf("renamed %s %s\n", item.Name, prefix+item.ID)
	return nil
}

// groupArgs recognises the group forms of mv: both arguments end with "/" or
// carry the jt://env/ prefix, and neither names a nested path.
func groupArgs(from, to string) (string, string, bool) {
	isGroup := func(s string) (string, bool) {
		if strings.HasPrefix(s, groupPrefix) {
			s = strings.TrimPrefix(s, groupPrefix) + "/"
		}
		if !strings.HasSuffix(s, "/") {
			return "", false
		}
		name := strings.TrimSuffix(s, "/")
		return name, name != "" && !strings.Contains(name, "/")
	}
	f, okFrom := isGroup(from)
	t, okTo := isGroup(to)
	return f, t, okFrom && okTo
}

// groupMembers returns the indexes of the entries under namespace.
func groupMembers(v vault, namespace string) []int {
	var members []int
	for i, item := range v.Secrets {
		if strings.HasPrefix(item.Name, namespace+"/") {
			members = append(members, i)
		}
	}
	return members
}
func resolve(args []string) error {
	if len(args) < 1 {
		return errors.New("resolve needs <name-or-ref>")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, key, err := openVault(c, false)
	if err != nil {
		return err
	}
	_, item, err := find(v, args[0])
	if err != nil {
		return err
	}
	value, err := open(key, item.Ciphertext)
	if err != nil {
		return err
	}
	rest := args[1:]
	envName := "JT_SECRET"
	command := []string{}
	for i := 0; i < len(rest); i++ {
		switch rest[i] {
		case "--env":
			if i+1 >= len(rest) {
				return errors.New("--env needs a name")
			}
			envName = rest[i+1]
			i++
		case "--exec":
			command = rest[i+1:]
			i = len(rest)
		default:
			return fmt.Errorf("unexpected argument %q", rest[i])
		}
	}
	if len(command) == 0 {
		return errors.New("resolve needs --exec COMMAND; jt never prints a secret value (use jt copy to put one on the clipboard for yourself)")
	}
	if !envPattern.MatchString(envName) {
		return errors.New("invalid environment variable name")
	}
	return runWithEnv(command, append(os.Environ(), envName+"="+value))
}

type exitStatusError struct{ code int }

func (e exitStatusError) Error() string { return fmt.Sprintf("command exited with status %d", e.code) }
func env(args []string) error {
	if len(args) < 3 || args[1] != "--" {
		return errors.New("env needs <namespace> -- COMMAND [ARGS...]")
	}
	namespace := strings.TrimPrefix(args[0], groupPrefix)
	c, err := loadConfig()
	if err != nil {
		return err
	}
	v, key, err := openVault(c, false)
	if err != nil {
		return err
	}
	environment := append([]string{}, os.Environ()...)
	injected := 0
	for _, item := range v.Secrets {
		if !strings.HasPrefix(item.Name, namespace+"/") {
			continue
		}
		name := strings.TrimPrefix(item.Name, namespace+"/")
		if !envPattern.MatchString(name) {
			return fmt.Errorf("invalid environment variable name in vault: %s", name)
		}
		value, err := open(key, item.Ciphertext)
		if err != nil {
			return err
		}
		environment = append(environment, name+"="+value)
		injected++
	}
	if injected == 0 {
		return fmt.Errorf("no secrets found for namespace %q", namespace)
	}
	return runWithEnv(args[2:], environment)
}

// runWithEnv executes command directly with the given environment. jt never
// starts a shell on its own: the caller names one (cmd /C, bash -c) when the
// command needs shell syntax, so there is no guessing which shell expands $VAR.
func runWithEnv(command, environment []string) error {
	cmd := exec.Command(command[0], command[1:]...)
	cmd.Env = environment
	cmd.Stdin, cmd.Stdout, cmd.Stderr = os.Stdin, os.Stdout, os.Stderr
	if err := cmd.Run(); err != nil {
		var exitErr *exec.ExitError
		if errors.As(err, &exitErr) {
			return exitStatusError{exitErr.ExitCode()}
		}
		if errors.Is(err, exec.ErrNotFound) && len(command) == 1 && strings.ContainsAny(command[0], " \t") {
			return fmt.Errorf("%w; jt runs the command directly, so a shell command needs an explicit shell: --exec cmd /C \"...\" or --exec bash -c '...'", err)
		}
		return err
	}
	return nil
}

func initVault(args []string) error {
	c, err := loadConfig()
	if err != nil {
		return err
	}
	repo := ""
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--repo":
			if i+1 >= len(args) {
				return errors.New("--repo needs URL")
			}
			repo = args[i+1]
			i++
		case "--vault":
			if i+1 >= len(args) {
				return errors.New("--vault needs DIR")
			}
			c.Vault = args[i+1]
			i++
		case "--key":
			if i+1 >= len(args) {
				return errors.New("--key needs FILE")
			}
			c.Key = args[i+1]
			i++
		default:
			return fmt.Errorf("unexpected argument %q", args[i])
		}
	}
	if err := saveConfig(c); err != nil {
		return err
	}
	if _, err := loadKey(c.Key, true); err != nil {
		return err
	}
	if err := os.MkdirAll(c.Vault, 0700); err != nil {
		return err
	}
	if _, err := os.Stat(filepath.Join(c.Vault, ".git")); errors.Is(err, os.ErrNotExist) {
		if branch := remoteBranch(repo); branch != "" {
			// The remote already holds a vault (another machine pushed it): clone it
			// instead of starting an empty, unrelated history.
			if err := runGit(".", "clone", "--branch", branch, repo, c.Vault); err != nil {
				return err
			}
		} else {
			if err := runGit(c.Vault, "init"); err != nil {
				return err
			}
			if repo != "" {
				if err := runGit(c.Vault, "remote", "add", "origin", repo); err != nil {
					return err
				}
			}
		}
	} else if repo != "" {
		_ = runGit(c.Vault, "remote", "set-url", "origin", repo)
	}
	// vault.json is written with LF on every platform; never let autocrlf rewrite or warn about it.
	if err := runGit(c.Vault, "config", "core.autocrlf", "false"); err != nil {
		return err
	}
	if _, err := os.Stat(filepath.Join(c.Vault, "vault.json")); errors.Is(err, os.ErrNotExist) {
		if err := saveVault(c, vault{Version: 1, Secrets: []entry{}}); err != nil {
			return err
		}
	}
	fmt.Printf("initialized vault %s\n", c.Vault)
	return nil
}
func runGit(dir string, args ...string) error {
	cmd := exec.Command("git", append([]string{"-C", dir}, args...)...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}
func remoteHasHeads(dir string) bool {
	return gitSucceeds("-C", dir, "ls-remote", "--exit-code", "--heads", "origin")
}

// gitSucceeds runs git quietly and reports whether it exited 0.
func gitSucceeds(args ...string) bool {
	cmd := exec.Command("git", args...)
	cmd.Stdout = io.Discard
	cmd.Stderr = io.Discard
	return cmd.Run() == nil
}

// remoteBranch names the branch to check out from repo: the remote HEAD when it
// points at an existing branch, otherwise the first branch listed. A bare repo
// whose HEAD still says main while the vault was pushed as master falls into
// the second case. Empty when the remote has no branches yet.
func remoteBranch(repo string) string {
	if repo == "" {
		return ""
	}
	out, err := exec.Command("git", "ls-remote", "--symref", repo).Output()
	if err != nil {
		return ""
	}
	first := ""
	for line := range strings.Lines(string(out)) {
		target, name, ok := strings.Cut(strings.TrimSpace(line), "\t")
		if !ok {
			continue
		}
		if name == "HEAD" && strings.HasPrefix(target, "ref: refs/heads/") {
			return strings.TrimPrefix(target, "ref: refs/heads/")
		}
		if branch, ok := strings.CutPrefix(name, "refs/heads/"); ok && first == "" {
			first = branch
		}
	}
	return first
}

func syncVault(args []string) error {
	if len(args) > 0 {
		return errors.New("sync takes no arguments")
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	if _, err := os.Stat(filepath.Join(c.Vault, ".git")); err != nil {
		return errors.New("vault is not a git repository; run jt init --repo URL")
	}
	if remoteHasHeads(c.Vault) {
		pull := []string{"pull", "--rebase", "--autostash", "origin"}
		// A vault created by init (not clone) has no upstream yet; name the branch explicitly.
		if _, err := gitOutput(c.Vault, "rev-parse", "--verify", "--quiet", "@{upstream}"); err != nil {
			branch, err := gitOutput(c.Vault, "symbolic-ref", "--short", "HEAD")
			if err != nil {
				return fmt.Errorf("cannot determine the current branch: %w", err)
			}
			pull = append(pull, branch)
		}
		if err := runGit(c.Vault, pull...); err != nil {
			return err
		}
	}
	if err := runGit(c.Vault, "add", "vault.json"); err != nil {
		return err
	}
	status := exec.Command("git", "-C", c.Vault, "diff", "--cached", "--quiet")
	if err := status.Run(); err != nil {
		commit := []string{"commit", "-m", "Update encrypted secrets"}
		// The author of an encrypted vault commit carries no information; do not
		// make a fresh machine fail until git has a global identity.
		if _, err := gitOutput(c.Vault, "config", "user.email"); err != nil {
			commit = append([]string{"-c", "user.name=jt", "-c", "user.email=jt@localhost"}, commit...)
		}
		if err := runGit(c.Vault, commit...); err != nil {
			return err
		}
	}
	// -u records the upstream so later pulls and status work without extra setup.
	return runGit(c.Vault, "push", "-u", "origin", "HEAD")
}

type vaultStatus struct {
	Vault string `json:"vault"`
	Key   string `json:"key"`
	Git   bool   `json:"git"`
	Dirty bool   `json:"dirty"`
	Ahead *int   `json:"ahead"`
}

// status only reads local git state; it never fetches, so "ahead" is relative to the last known remote ref.
func status(args []string) error {
	asJSON := false
	for _, arg := range args {
		if arg != "--json" {
			return fmt.Errorf("unexpected argument %q", arg)
		}
		asJSON = true
	}
	c, err := loadConfig()
	if err != nil {
		return err
	}
	st := vaultStatus{Vault: c.Vault, Key: c.Key}
	if _, err := os.Stat(filepath.Join(c.Vault, ".git")); err == nil {
		st.Git = true
		out, err := gitOutput(c.Vault, "status", "--porcelain", "--", "vault.json")
		if err != nil {
			return err
		}
		st.Dirty = out != ""
		st.Ahead = commitsAhead(c.Vault)
	}
	if asJSON {
		data, err := json.MarshalIndent(st, "", "  ")
		if err != nil {
			return err
		}
		_, err = fmt.Fprintf(stdout, "%s\n", data)
		return err
	}
	fmt.Fprintf(stdout, "vault  %s\nkey    %s\n", st.Vault, st.Key)
	switch {
	case !st.Git:
		fmt.Fprintln(stdout, "sync   not a git repository")
	case st.Dirty:
		fmt.Fprintln(stdout, "sync   uncommitted changes; run jt sync")
	case st.Ahead != nil && *st.Ahead > 0:
		fmt.Fprintf(stdout, "sync   %d commit(s) not pushed; run jt sync\n", *st.Ahead)
	default:
		fmt.Fprintln(stdout, "sync   up to date with the last known remote state")
	}
	return nil
}
func gitOutput(dir string, args ...string) (string, error) {
	out, err := exec.Command("git", append([]string{"-C", dir}, args...)...).Output()
	return strings.TrimSpace(string(out)), err
}

// commitsAhead counts local commits missing from the upstream (or origin/<branch>); nil when there is no remote ref to compare with.
func commitsAhead(dir string) *int {
	base := "@{upstream}"
	if _, err := gitOutput(dir, "rev-parse", "--verify", "--quiet", base); err != nil {
		branch, err := gitOutput(dir, "symbolic-ref", "--short", "HEAD")
		if err != nil {
			return nil
		}
		base = "refs/remotes/origin/" + branch
		if _, err := gitOutput(dir, "rev-parse", "--verify", "--quiet", base); err != nil {
			return nil
		}
	}
	out, err := gitOutput(dir, "rev-list", "--count", base+"..HEAD")
	if err != nil {
		return nil
	}
	var n int
	if _, err := fmt.Sscanf(out, "%d", &n); err != nil {
		return nil
	}
	return &n
}
