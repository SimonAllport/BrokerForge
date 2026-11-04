// Builds the Word guide from its Markdown source (docs/guide/how-to-guide.md), so the two never drift apart.
// Run from tools/media: node build-guide.mjs   (after node record.mjs --shots has taken the pictures)
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  AlignmentType, BorderStyle, Document, Footer, HeadingLevel, ImageRun, LevelFormat, Packer, PageNumber, Paragraph, ShadingType,
  Table, TableCell, TableOfContents, TableRow, TextRun, WidthType,
} from 'docx';
import { marked } from 'marked';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const DOCS = join(root, 'docs', 'guide');
const FONT = 'Calibri';
const MONO = 'Consolas';
const ACCENT = '1F4E79';
const TEXT_WIDTH = 9026; // A4 with 1" margins, in DXA
const MAX_IMAGE = 600; // px at 96 dpi

const GUIDES = [
  { source: 'how-to-guide.md', output: 'BrokerForge How-to Guide.docx', footer: 'BrokerForge · How-to guide', toc: '1-2' },
];

const pngSize = file => { const b = readFileSync(file); return { w: b.readUInt32BE(16), h: b.readUInt32BE(20) }; };

/** Inline Markdown tokens as Word runs: bold, italic, code, links (as their text). */
function runs(tokens = [], style = {}) {
  const out = [];
  for (const t of tokens) {
    switch (t.type) {
      case 'strong': out.push(...runs(t.tokens, { ...style, bold: true })); break;
      case 'em': out.push(...runs(t.tokens, { ...style, italics: true })); break;
      case 'codespan': out.push(new TextRun({ text: decode(t.text), font: MONO, size: 20, color: '333333', ...style })); break;
      case 'link': out.push(...runs(t.tokens, { ...style, color: '2E74B5' })); break;
      case 'br': out.push(new TextRun({ text: '', break: 1 })); break;
      case 'text': out.push(...(t.tokens ? runs(t.tokens, style) : [new TextRun({ text: decode(t.text), ...style })])); break;
      case 'escape': out.push(new TextRun({ text: decode(t.text), ...style })); break;
      default: if (t.text) out.push(new TextRun({ text: decode(t.text), ...style }));
    }
  }
  return out;
}
const decode = s => s.replace(/&#39;/g, "'").replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>');

function image(src, caption) {
  const file = join(DOCS, src);
  // A screenshot still to be captured (the media step makes it): leave it out, and say so, rather than fail the build.
  if (!existsSync(file)) {
    console.warn(`warning: ${src} is missing; left out of the Word guide (caption kept).`);
    return undefined;
  }
  const { w, h } = pngSize(file);
  // Screenshots are captured at twice screen resolution: half their pixels is their size on screen.
  const width = Math.min(MAX_IMAGE, Math.round(w / 2));
  const height = Math.round(h * (width / w));
  return new Paragraph({
    alignment: AlignmentType.CENTER, spacing: { before: 120, after: 60 }, keepNext: true,
    children: [new ImageRun({ type: 'png', data: readFileSync(file), transformation: { width, height }, altText: { title: caption, description: caption, name: src } })],
  });
}

/** A code block as shaded monospace lines, indented by `left` (DXA). */
function codeBlock(text, left) {
  const lines = text.split('\n');
  return lines.map((l, n) => new Paragraph({
    keepNext: n < lines.length - 1, keepLines: true,
    shading: { type: ShadingType.CLEAR, fill: 'F3F3F3', color: 'auto' },
    spacing: { before: n === 0 ? 60 : 0, after: n === lines.length - 1 ? 160 : 0 }, indent: { left, right: 200 },
    children: [new TextRun({ text: l === '' ? ' ' : l, font: MONO, size: 18 })],
  }));
}

const border = { style: BorderStyle.SINGLE, size: 4, color: 'BFBFBF' };
const borders = { top: border, bottom: border, left: border, right: border };
function table(t) {
  const n = t.header.length;
  // Column widths from the longest text in each column, within sensible limits.
  const longest = t.header.map((h, i) => Math.max(h.text.length, ...t.rows.map(r => r[i].text.length)));
  const weights = longest.map(l => Math.min(Math.max(l, 8), 60));
  const total = weights.reduce((a, b) => a + b, 0);
  const widths = weights.map(x => Math.floor((x / total) * TEXT_WIDTH));
  widths[n - 1] += TEXT_WIDTH - widths.reduce((a, b) => a + b, 0);
  const cell = (c, i, head) => new TableCell({
    width: { size: widths[i], type: WidthType.DXA }, borders,
    shading: head ? { type: ShadingType.CLEAR, fill: 'DCE6F2', color: 'auto' } : undefined,
    margins: { top: 60, bottom: 60, left: 100, right: 100 },
    children: [new Paragraph({ children: runs(c.tokens, head ? { bold: true } : {}) })],
  });
  return new Table({
    width: { size: TEXT_WIDTH, type: WidthType.DXA }, columnWidths: widths,
    rows: [new TableRow({ tableHeader: true, children: t.header.map((c, i) => cell(c, i, true)) }),
      ...t.rows.map(r => new TableRow({ cantSplit: true, children: r.map((c, i) => cell(c, i, false)) }))],
  });
}

function build(guide) {
  const tokens = marked.lexer(readFileSync(join(DOCS, guide.source), 'utf8'));
  const body = [];
  const lists = [];
  let skipList = false;
  let pendingNewPage = false;
  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i];
    if (t.type === 'space') continue;
    if (t.type === 'heading') {
      const text = decode(t.text);
      if (t.depth === 1) {
        body.push(new Paragraph({ spacing: { before: 1800, after: 200 }, children: [new TextRun({ text, bold: true, size: 56, color: ACCENT })] }));
        continue;
      }
      if (t.depth === 2 && text === 'Contents') {
        // Word's own table of contents replaces the Markdown list of links.
        body.push(new Paragraph({ spacing: { before: 300, after: 120 }, children: [new TextRun({ text: 'Contents', bold: true, size: 28, color: ACCENT })] }));
        body.push(new TableOfContents('Contents', { hyperlink: true, headingStyleRange: guide.toc }));
        skipList = true;
        pendingNewPage = true;
        continue;
      }
      const level = t.depth === 2 ? HeadingLevel.HEADING_1 : t.depth === 3 ? HeadingLevel.HEADING_2 : HeadingLevel.HEADING_3;
      body.push(new Paragraph({ heading: level, keepNext: true, pageBreakBefore: pendingNewPage, children: runs(t.tokens) }));
      pendingNewPage = false;
      continue;
    }
    if (t.type === 'list') {
      if (skipList) { skipList = false; continue; }
      const ref = t.ordered ? `n${lists.length}` : 'bullets';
      // An ordered list split by a picture carries on from its own number (3. after two steps and a screenshot).
      if (t.ordered) lists.push({ reference: ref, start: typeof t.start === 'number' ? t.start : 1 });
      for (const item of t.items) {
        let first = true;
        let inline = [];
        const flush = () => {
          if (!inline.length && !first) return;
          body.push(new Paragraph(first
            ? { numbering: { reference: ref, level: 0 }, spacing: { after: 80 }, children: runs(inline) }
            : { indent: { left: 720 }, spacing: { after: 80 }, children: runs(inline) }));
          first = false;
          inline = [];
        };
        for (const x of item.tokens) {
          if (x.type === 'text' || x.type === 'paragraph') inline.push(...(x.tokens ?? [x]));
          else if (x.type === 'code') { flush(); body.push(...codeBlock(x.text, 920)); }
          else if (x.type === 'list') {
            flush();
            for (const sub of x.items) {
              const subInline = sub.tokens.flatMap(y => (y.type === 'text' || y.type === 'paragraph' ? (y.tokens ?? [y]) : []));
              body.push(new Paragraph({ bullet: { level: 1 }, indent: { left: 1080, hanging: 360 }, spacing: { after: 60 }, children: runs(subInline) }));
            }
          }
        }
        flush();
      }
      continue;
    }
    if (t.type === 'paragraph') {
      const only = t.tokens.filter(x => !(x.type === 'text' && !x.text.trim()));
      if (only.length === 1 && only[0].type === 'image') {
        const picture = image(only[0].href, decode(only[0].text));
        if (picture) body.push(picture);
        continue;
      }
      const caption = t.tokens.length === 1 && t.tokens[0].type === 'em';
      body.push(caption
        ? new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 200 }, children: runs(t.tokens[0].tokens, { italics: true, size: 18, color: '595959' }) })
        : new Paragraph({ spacing: { after: 120 }, children: runs(t.tokens) }));
      continue;
    }
    if (t.type === 'blockquote') {
      const inline = t.tokens.flatMap(x => x.tokens ?? []);
      const warning = /^⚠️/.test(t.text);
      body.push(new Paragraph({
        shading: { type: ShadingType.CLEAR, fill: warning ? 'FFF4E5' : 'EAF1FB', color: 'auto' },
        border: { left: { style: BorderStyle.SINGLE, size: 18, color: warning ? 'C77700' : '2E74B5', space: 8 } },
        spacing: { before: 120, after: 160 }, indent: { left: 200, right: 200 },
        children: runs(warning ? inline.map(x => (x.type === 'text' ? { ...x, text: x.text.replace(/^⚠️\s*/, ''), tokens: undefined } : x)) : inline),
      }));
      continue;
    }
    if (t.type === 'code') { body.push(...codeBlock(t.text, 200)); continue; }
    if (t.type === 'table') { body.push(table(t), new Paragraph({ spacing: { after: 120 }, children: [] })); continue; }
    if (t.type === 'hr') continue;
    if (t.type === 'html') continue;
    throw new Error(`${guide.source}: unhandled Markdown block '${t.type}'`);
  }

  const doc = new Document({
    creator: 'BrokerForge', title: decode(tokens.find(t => t.type === 'heading')?.text ?? guide.output),
    styles: {
      default: { document: { run: { font: FONT, size: 22 } } },
      paragraphStyles: [
        { id: 'Heading1', name: 'Heading 1', basedOn: 'Normal', next: 'Normal', quickFormat: true, run: { size: 36, bold: true, color: ACCENT, font: FONT }, paragraph: { spacing: { before: 480, after: 160 }, outlineLevel: 0 } },
        { id: 'Heading2', name: 'Heading 2', basedOn: 'Normal', next: 'Normal', quickFormat: true, run: { size: 28, bold: true, color: '2E74B5', font: FONT }, paragraph: { spacing: { before: 280, after: 120 }, outlineLevel: 1, keepNext: true } },
        { id: 'Heading3', name: 'Heading 3', basedOn: 'Normal', next: 'Normal', quickFormat: true, run: { size: 24, bold: true, color: ACCENT, font: FONT }, paragraph: { spacing: { before: 240, after: 100 }, outlineLevel: 2, keepNext: true } },
      ],
    },
    numbering: {
      config: [
        { reference: 'bullets', levels: [{ level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] },
        ...lists.map(({ reference, start }) => ({ reference, levels: [{ level: 0, format: LevelFormat.DECIMAL, text: '%1.', start, alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] })),
      ],
    },
    features: { updateFields: true },
    sections: [{
      properties: { page: { size: { width: 11906, height: 16838 }, margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } } },
      footers: { default: new Footer({ children: [new Paragraph({ alignment: AlignmentType.CENTER, children: [new TextRun({ text: `${guide.footer} · `, size: 16, color: '808080' }), new TextRun({ children: [PageNumber.CURRENT], size: 16, color: '808080' })] })] }) },
      children: body,
    }],
  });
  return Packer.toBuffer(doc);
}

for (const guide of GUIDES) {
  writeFileSync(join(DOCS, guide.output), await build(guide));
  console.log(`docs/guide/${guide.output}`);
}
