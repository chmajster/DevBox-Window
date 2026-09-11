from pathlib import Path

path = Path("tests/DevBox.Tests/PostMergeRegressionTests.cs")
text = path.read_text(encoding="utf-8")

needle = "using System.Text.Json;\n"
if text.count(needle) != 1:
    raise RuntimeError("Expected one System.Text.Json using directive.")
text = text.replace(needle, needle + "using Xunit;\n", 1)

old = '''            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.Php, null, null, "none", null, false, Array.Empty<string>()));'''
new = '''            _ = workspace.Create(new ProjectCreateRequest(
                Name: "app",
                Domain: "app.test",
                Kind: ProjectKind.Php,
                Https: false,
                DatabaseEngine: "none",
                Addons: Array.Empty<string>()));'''
if text.count(old) != 1:
    raise RuntimeError("Expected one positional ProjectCreateRequest construction.")
text = text.replace(old, new, 1)

path.write_text(text, encoding="utf-8", newline="\n")
