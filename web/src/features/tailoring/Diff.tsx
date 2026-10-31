import type { Schemas } from '@/api/client';

type Segment = Schemas['DiffSegmentDto'];

/**
 * One side of a word diff (prototype `diffHtml`): the original shows equal and deleted text, the proposal equal and
 * inserted text. Whitespace between two changed words is highlighted with them. Text only: nothing is rendered as HTML.
 */
export function DiffSide({ diff, side }: { diff: Segment[]; side: 'original' | 'proposed' }) {
  const changed = side === 'original' ? 'delete' : 'insert';
  const skip = side === 'original' ? 'insert' : 'delete';
  const shown = diff.filter((s) => s.type !== skip);
  return (
    <>
      {shown.map((s, i) => {
        const between =
          s.type === 'equal' &&
          /^\s+$/.test(s.text) &&
          shown[i - 1]?.type === changed &&
          shown[i + 1]?.type === changed;
        if (s.type !== changed && !between) return <span key={i}>{s.text}</span>;
        return changed === 'delete' ? <del key={i}>{s.text}</del> : <ins key={i}>{s.text}</ins>;
      })}
    </>
  );
}
