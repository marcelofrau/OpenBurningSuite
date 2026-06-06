import os, re, shutil, sys

views_dir = 'OpenBurningSuite/Views'
icons_16 = 'OpenBurningSuite/Assets/Icons/16'
icons_50 = 'OpenBurningSuite/Assets/Icons/50'
out_dir = 'icons-review'

os.makedirs(out_dir, exist_ok=True)

def find_text_context(lines, i, max_look=8):
    """Find the nearest TextBlock or Content/Header attribute near line i."""
    # Check same line
    tb = re.search(r'Text="([^"]+)"', lines[i])
    if tb:
        return tb.group(1)
    # Look forward
    for j in range(i+1, min(i+max_look, len(lines))):
        tb = re.search(r'Text="([^"]+)"', lines[j])
        if tb:
            return tb.group(1)
    # Look backward
    for j in range(max(0,i-max_look), i):
        tb = re.search(r'Text="([^"]+)"', lines[j])
        if tb:
            return tb.group(1)
    # Check Content= or Header= backward
    for j in range(max(0,i-max_look), i):
        attr = re.search(r'(?:Content|Header)="([^"]+)"', lines[j])
        if attr:
            return attr.group(1)
    # Check forward for Content=
    for j in range(i+1, min(i+max_look, len(lines))):
        attr = re.search(r'(?:Content|Header)="([^"]+)"', lines[j])
        if attr:
            return attr.group(1)
    return ''

def clean_filename(s):
    s = re.sub(r'[<>:"/\\|?*&;#\s]+', '_', s)
    s = s.strip('_')
    # Truncate long names but try to break at word boundary
    if len(s) > 50:
        s = s[:50].rstrip('_')
    return s

for fname in sorted(os.listdir(views_dir)):
    if not fname.endswith('.axaml'):
        continue
    view_name = fname.replace('.axaml', '')
    fpath = os.path.join(views_dir, fname)
    with open(fpath) as f:
        lines = f.readlines()

    view_out = os.path.join(out_dir, view_name)
    os.makedirs(view_out, exist_ok=True)
    entries = []

    for i, line in enumerate(lines):
        m = re.search(r'avares://OpenBurningSuite/Assets/Icons/(\d+)/([^"]+)', line)
        if not m:
            continue
        size = m.group(1)
        icon_file = m.group(2)
        icon_stem = icon_file.replace('-' + size + '.png', '')
        context = find_text_context(lines, i)

        label = context if context else icon_stem
        clean = clean_filename(label)
        dst_name = '{}_{}_{}.png'.format(clean, icon_stem, size)
        dst_path = os.path.join(view_out, dst_name)

        src = os.path.join(icons_16 if size == '16' else icons_50, icon_file)
        if os.path.exists(src):
            shutil.copy2(src, dst_path)
            entries.append((i+1, icon_stem, size, context, dst_name))

    if entries:
        with open(os.path.join(view_out, '_icons-map.md'), 'w') as mf:
            mf.write('# {} — Icon Map\n\n'.format(view_name))
            mf.write('Source: `{}`\n\n'.format(fname))
            mf.write('| # | Line | Stem | Size | Context | File |\n')
            mf.write('|---|------|------|------|---------|------|\n')
            for idx, (line, stem, size, ctx, fn) in enumerate(entries, 1):
                ctx_esc = ctx.replace('|', '\\|') if ctx else '\u2014'
                mf.write('| {} | {} | `{}` | {}px | {} | `{}` |\n'.format(
                    idx, line, stem, size, ctx_esc, fn))
        print('{}: {} icons'.format(view_name, len(entries)))

print('\nDone. Total views: {}'.format(
    len([f for f in os.listdir(out_dir) if os.path.isdir(os.path.join(out_dir, f))])))
