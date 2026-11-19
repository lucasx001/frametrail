export const SEEK_HREF_PREFIX = 'https://ai-seek.frametrail.local/';

const TIMESTAMP_REGEX = /\b(\d{1,2}):(\d{2}(?:\.\d+)?)\b/g;

export function parseTimestampToSeconds(raw: string): number | null {
  const parts = raw.trim().split(':');
  if (parts.length !== 2) return null;
  const minutes = Number.parseInt(parts[0], 10);
  const seconds = Number.parseFloat(parts[1]);
  if (!Number.isFinite(minutes) || !Number.isFinite(seconds) || minutes < 0 || seconds < 0) {
    return null;
  }
  return minutes * 60 + seconds;
}

export function extractSeekSeconds(href?: string): number | null {
  if (!href || !href.startsWith(SEEK_HREF_PREFIX)) {
    return null;
  }
  const value = Number.parseFloat(href.slice(SEEK_HREF_PREFIX.length));
  return Number.isFinite(value) && value >= 0 ? value : null;
}

/**
 * Rewrites standalone mm:ss / mm:ss.s timestamps inside markdown text into
 * clickable #frametrail-seek-<seconds> links while leaving fenced code blocks,
 * inline code spans, and existing markdown links untouched.
 */
export function linkifyTimestamps(markdown: string): string {
  if (!markdown) return '';

  // Split around fenced code blocks (```...```), inline code (`...`), and existing links ([...](...))
  const tokenSplitter = /(```[\s\S]*?```|`[^`\n]+`|\[[^\]]*\]\([^)]*\))/g;
  const parts = markdown.split(tokenSplitter);

  return parts
    .map((part, idx) => {
      // Odd indices are matched code/link segments — leave untouched
      if (idx % 2 === 1) {
        return part;
      }
      return part.replace(TIMESTAMP_REGEX, (match) => {
        const seconds = parseTimestampToSeconds(match);
        if (seconds === null) return match;
        const formatted = Number(seconds.toFixed(3));
        return `[${match}](${SEEK_HREF_PREFIX}${formatted})`;
      });
    })
    .join('');
}
