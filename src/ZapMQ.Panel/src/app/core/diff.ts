/** One line of a comparison: kept, taken out or put in, with its number on each side. */
export interface DiffLine {
  kind: ' ' | '+' | '-';
  text: string;
  before: number | null;
  after: number | null;
}

/** A stretch of a comparison, or how many unchanged lines were left out between two of them. */
export type DiffPart = { lines: DiffLine[] } | { skipped: number };

/** Above this many cells the comparison is not worth its cost: everything out, everything in. */
const TOO_MUCH = 8_000_000;

const split = (text: string) => text.replace(/\r\n?/g, '\n').replace(/\n$/, '').split('\n');

/** The lines that differ between two texts, found by their longest common subsequence. */
export function diff(before: string, after: string): DiffLine[] {
  const a = split(before);
  const b = split(after);
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) {
    start++;
  }
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }

  const lines: DiffLine[] = [];
  for (let i = 0; i < start; i++) {
    lines.push({ kind: ' ', text: a[i], before: i + 1, after: i + 1 });
  }

  const n = endA - start;
  const m = endB - start;
  if (n * m > TOO_MUCH) {
    for (let i = 0; i < n; i++) {
      lines.push({ kind: '-', text: a[start + i], before: start + i + 1, after: null });
    }
    for (let j = 0; j < m; j++) {
      lines.push({ kind: '+', text: b[start + j], before: null, after: start + j + 1 });
    }
  } else {
    // length[i][j]: how much the rest of each side, from i and from j, has in common.
    const width = m + 1;
    const length = new Uint32Array((n + 1) * width);
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        length[i * width + j] = a[start + i] === b[start + j] ? length[(i + 1) * width + j + 1] + 1 : Math.max(length[(i + 1) * width + j], length[i * width + j + 1]);
      }
    }
    let i = 0;
    let j = 0;
    while (i < n || j < m) {
      if (i < n && j < m && a[start + i] === b[start + j]) {
        lines.push({ kind: ' ', text: a[start + i], before: start + i + 1, after: start + j + 1 });
        i++;
        j++;
      } else if (i < n && (j >= m || length[(i + 1) * width + j] >= length[i * width + j + 1])) {
        lines.push({ kind: '-', text: a[start + i], before: start + i + 1, after: null });
        i++;
      } else {
        lines.push({ kind: '+', text: b[start + j], before: null, after: start + j + 1 });
        j++;
      }
    }
  }

  for (let i = endA; i < a.length; i++) {
    lines.push({ kind: ' ', text: a[i], before: i + 1, after: endB + (i - endA) + 1 });
  }
  return lines;
}

/** Only what changed, with a few lines around each change to tell where it is. */
export function around(lines: DiffLine[], context = 3): DiffPart[] {
  const keep = new Uint8Array(lines.length);
  lines.forEach((line, index) => {
    if (line.kind !== ' ') {
      for (let near = Math.max(0, index - context); near <= Math.min(lines.length - 1, index + context); near++) {
        keep[near] = 1;
      }
    }
  });

  // A handful of equal lines between two changes is not worth leaving out.
  for (let index = 0; index < lines.length; ) {
    if (keep[index]) {
      index++;
      continue;
    }
    let end = index;
    while (end < lines.length && !keep[end]) {
      end++;
    }
    if (end - index <= context && index > 0 && end < lines.length) {
      keep.fill(1, index, end);
    }
    index = end;
  }

  const parts: DiffPart[] = [];
  let index = 0;
  while (index < lines.length) {
    const from = index;
    const kept = keep[index];
    while (index < lines.length && keep[index] === kept) {
      index++;
    }
    parts.push(kept ? { lines: lines.slice(from, index) } : { skipped: index - from });
  }
  return parts;
}
