from pathlib import Path
p=Path('CHANGELOG.md')
text=p.read_text(encoding='utf-8')
needle='### Fixed\n\n'
entries='''- Windows autostart updates now roll back both the in-memory setting and the HKCU Run value when settings persistence fails, avoiding split registry/file state.\n- Elevated hosts-file helper processes now have a 30-second lifetime bound and are terminated on timeout instead of leaving UI operations waiting indefinitely.\n'''
pos=text.find(needle)
if pos < 0: raise RuntimeError('Fixed section not found')
pos += len(needle)
p.write_text(text[:pos]+entries+text[pos:], encoding='utf-8')
