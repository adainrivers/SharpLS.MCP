# SharpLS.MCP

MCP server that wraps [roslyn-language-server](https://github.com/dotnet/roslyn/tree/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer) to provide C# code intelligence tools.

## Requirements

- .NET 10
- `roslyn-language-server` installed globally: `dotnet tool install -g roslyn-language-server --prerelease`

## Usage

Pass the solution/project path as a CLI argument:

```
SharpLS.MCP.exe /path/to/MyProject.slnx
```

Or use `load_solution` tool at runtime to load/switch solutions.

## Claude MCP config

```json
{
  "mcpServers": {
    "SharpLS": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/SharpLS.MCP", "--", "/path/to/MyProject.slnx"]
    }
  }
}
```

Or using a pre-built binary:

```json
{
  "mcpServers": {
    "SharpLS": {
      "command": "/path/to/SharpLS.MCP/bin/Debug/net10.0/SharpLS.MCP.exe",
      "args": ["/path/to/MyProject.slnx"]
    }
  }
}
```

## Environment variables

| Variable | Description |
|---|---|
| `SHARPLSMCP_TIMEOUT` | LSP request timeout in seconds (default: 60) |

## Tools

### Navigation

| Tool | Description |
|---|---|
| `find_definition` | Find the definition of a symbol by name in a file |
| `find_references` | Find all references to a symbol across the workspace |
| `get_hover` | Get hover documentation and type info for a symbol |
| `find_document_symbols` | List all symbols defined in a C# file |
| `find_workspace_symbols` | Search for symbols across the entire workspace |
| `go_to_implementation` | Find implementations of an interface or abstract method |
| `go_to_type_definition` | Jump to the type definition of a symbol |

### Hierarchy

| Tool | Description |
|---|---|
| `incoming_calls` | Find all functions/methods that call the specified symbol |
| `outgoing_calls` | Find all functions/methods called by the specified symbol |
| `supertypes` | Find base types and interfaces of a type |
| `subtypes` | Find derived types and implementations of a type |
| `type_hierarchy` | Show full inheritance chain from a type up to its root base type |

### Editing & Diagnostics

| Tool | Description |
|---|---|
| `get_diagnostics` | Get compiler errors and warnings for a file without building |
| `get_workspace_diagnostics` | Get errors and warnings across the entire workspace/solution |
| `rename_symbol` | Rename a symbol across the workspace and apply changes to disk |
| `format_document` | Format a file (or line range) using project formatting rules |

### Code Actions

| Tool | Description |
|---|---|
| `get_code_actions` | List available quick fixes and refactorings for a line or range |
| `apply_code_action` | Apply a code action by title (quick fix, refactoring, etc.) |

### IntelliSense

| Tool | Description |
|---|---|
| `get_completion` | Get code completion suggestions at a symbol position |
| `get_signature_help` | Get method signature overloads and parameter info |

### Lifecycle

| Tool | Description |
|---|---|
| `load_solution` | Load a solution or project file (.sln/.slnx/.csproj) |
| `restart_lsp` | Restart the language server |
