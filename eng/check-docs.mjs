import { readFile, readdir, stat } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// Check the generated artifact, including paths and fragments that Antora's
// AsciiDoc reference resolver cannot check (for example, UI assets and links).
const root = fileURLToPath(new URL('../build/site/', import.meta.url))
const origin = 'https://docs.invalid'
const playbook = await readFile(new URL('../antora-playbook.yml', import.meta.url), 'utf8')
const sitePath = new URL(playbook.match(/^  url: (\S+)$/m)[1]).pathname.replace(/\/$/, '')
const errors = []
const documents = new Map()

async function collect(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const filename = path.join(directory, entry.name)
    if (entry.isDirectory()) await collect(filename)
    else if (entry.name.endsWith('.html')) {
      const html = await readFile(filename, 'utf8')
      documents.set(filename, {
        html,
        ids: new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1])),
      })
    }
  }
}

await collect(root)
for (const [filename, { html }] of documents) {
  const relative = path.relative(root, filename).split(path.sep).join('/')
  if (/class="[^"]*\bunresolved\b/.test(html)) errors.push(`${relative}: unresolved reference`)
  if (/\b(?:href|src)="file:/.test(html)) errors.push(`${relative}: machine-local URL in published content`)
  for (const match of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
    const reference = match[1].replaceAll('&amp;', '&')
    const url = new URL(reference, `${origin}/${relative}`)
    if (url.origin !== origin) continue
    // Antora's 404 uses production-root paths because it can be served from any URL.
    const pathname = url.pathname.startsWith(`${sitePath}/`) ? url.pathname.slice(sitePath.length) : url.pathname
    let target = path.resolve(root, `.${decodeURIComponent(pathname)}`)
    try {
      if ((await stat(target)).isDirectory()) target = path.join(target, 'index.html')
      await stat(target)
    } catch {
      errors.push(`${relative}: missing target ${reference}`)
      continue
    }
    const fragment = decodeURIComponent(url.hash.slice(1))
    if (fragment && documents.has(target) && !documents.get(target).ids.has(fragment)) {
      errors.push(`${relative}: missing fragment ${reference}`)
    }
  }
}

for (const required of ['.nojekyll', 'search-index.js', 'sitemap.xml', 'redis-iris/index.html']) {
  try { await stat(path.join(root, required)) }
  catch { errors.push(`Missing site artifact: ${required}`) }
}

if (errors.length) {
  console.error(errors.join('\n'))
  process.exitCode = 1
} else {
  console.log(`Checked ${documents.size} HTML files: local links, fragments, assets, and publishing artifacts are valid.`)
}
