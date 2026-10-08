/** A piece of a script and what it is, for colouring. */
export interface SqlPiece {
  text: string;
  kind: '' | 'keyword' | 'string' | 'comment' | 'number' | 'variable' | 'name';
}

const KEYWORDS = new Set(
  `add all alter and any as asc authorization begin between break by cascade case catch check close clustered column commit constraint continue
  create cross cursor deallocate declare default delete desc distinct drop else end exec execute exists fetch for foreign from full function go grant
  group having identity if in include index inner insert instead into is join key left like merge nocheck nocount nonclustered not null of off on open
  option or order out outer output over partition persisted primary print proc procedure raiserror readonly references return returns right rollback
  rowcount schemabinding select set table then throw top tran transaction trigger truncate try type union unique update use values view when where
  while with after apply collate except intersect pivot unpivot using waitfor`.split(/\s+/),
);

const TOKEN = /(--[^\n]*|\/\*[\s\S]*?\*\/)|(N?'(?:[^']|'')*')|(\[[^\]]*\]|"[^"]*")|(@@?[\w#$]+)|(\b\d+(?:\.\d+)?\b)|([A-Za-z_#][\w#$]*)/g;

/** Splits a script into pieces that tell keywords, texts, comments and names apart. */
export function colour(script: string): SqlPiece[] {
  const pieces: SqlPiece[] = [];
  let last = 0;
  for (const match of script.matchAll(TOKEN)) {
    if (match.index > last) {
      pieces.push({ text: script.slice(last, match.index), kind: '' });
    }
    const [text, comment, string, name, variable, number, word] = match;
    pieces.push({
      text,
      kind: comment ? 'comment' : string ? 'string' : name ? 'name' : variable ? 'variable' : number ? 'number' : word && KEYWORDS.has(word.toLowerCase()) ? 'keyword' : '',
    });
    last = match.index + text.length;
  }
  if (last < script.length) {
    pieces.push({ text: script.slice(last), kind: '' });
  }
  return pieces;
}
