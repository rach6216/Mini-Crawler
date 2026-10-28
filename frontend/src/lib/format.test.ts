import { describe, expect, it } from 'vitest';
import { EMPTY_VALUE, formatDuration, formatRatio, formatRelative, splitUrl } from './format';

describe('formatRatio', () => {
  it('formats ratios as percentages with one decimal', () => {
    expect(formatRatio(0.75)).toBe('75.0%');
    expect(formatRatio(1 / 3)).toBe('33.3%');
    expect(formatRatio(0)).toBe('0.0%');
    expect(formatRatio(1)).toBe('100.0%');
  });

  it('clamps invalid input', () => {
    expect(formatRatio(Number.NaN)).toBe('0.0%');
    expect(formatRatio(1.5)).toBe('100.0%');
  });
});

describe('formatDuration', () => {
  const start = '2026-10-28T18:00:00.000Z';

  it('formats seconds, minutes and hours', () => {
    expect(formatDuration(start, '2026-10-28T18:00:00.400Z')).toBe('< 1 s');
    expect(formatDuration(start, '2026-10-28T18:00:42.000Z')).toBe('42 s');
    expect(formatDuration(start, '2026-10-28T18:03:05.000Z')).toBe('3 m 05 s');
    expect(formatDuration(start, '2026-10-28T19:02:00.000Z')).toBe('1 h 02 m');
  });

  it('measures to now while running and shows a placeholder before start', () => {
    expect(formatDuration(start, null, Date.parse('2026-10-28T18:00:10.000Z'))).toBe('10 s');
    expect(formatDuration(null, null)).toBe(EMPTY_VALUE);
  });
});

describe('formatRelative', () => {
  const now = Date.parse('2026-10-28T18:00:00.000Z');

  it('describes elapsed time', () => {
    expect(formatRelative('2026-10-28T17:59:58.000Z', now)).toBe('just now');
    expect(formatRelative('2026-10-28T17:59:30.000Z', now)).toBe('30 s ago');
    expect(formatRelative('2026-10-28T17:55:00.000Z', now)).toBe('5 min ago');
    expect(formatRelative('2026-10-28T15:00:00.000Z', now)).toBe('3 h ago');
    expect(formatRelative('2026-10-26T18:00:00.000Z', now)).toBe('2 d ago');
  });
});

describe('splitUrl', () => {
  it('separates host from path and query', () => {
    expect(splitUrl('https://example.com/a/b?x=1')).toEqual({ host: 'example.com', path: '/a/b?x=1' });
    expect(splitUrl('not a url')).toEqual({ host: '', path: 'not a url' });
  });
});
