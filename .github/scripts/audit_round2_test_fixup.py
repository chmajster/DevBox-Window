from pathlib import Path
p = Path('tests/DevBox.Tests/EnvironmentPlatformFollowupTests.cs')
text = p.read_text(encoding='utf-8')
old = '''            using var locked = new FileStream(certificatePath, FileMode.Open, FileAccess.Read, FileShare.None);\n            Assert.ThrowsAny<IOException>(() => rollback.Restore(state));\n            Assert.True(File.Exists(certificatePath));\n'''
new = '''            using var locked = new FileStream(certificatePath, FileMode.Open, FileAccess.Read, FileShare.None);\n            var aggregate = Assert.Throws<AggregateException>(() => rollback.Restore(state));\n            Assert.Contains(aggregate.InnerExceptions, ex => ex is IOException);\n            Assert.True(File.Exists(certificatePath));\n'''
if text.count(old) != 1:
    raise RuntimeError(f'Expected one TLS rollback assertion, found {text.count(old)}')
p.write_text(text.replace(old, new, 1), encoding='utf-8', newline='\n')
