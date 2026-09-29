import { describe, expect, it } from 'vitest';
import { hasThumbnail, viewKindOf } from './kinds';

describe('viewKindOf', () => {
  it('knows images, PDF, audio, video and text by extension, whatever the case', () => {
    expect(viewKindOf('Beach.JPG')).toBe('image');
    expect(viewKindOf('drawing.svg')).toBe('image');
    expect(viewKindOf('manual.pdf')).toBe('pdf');
    expect(viewKindOf('song.flac')).toBe('audio');
    expect(viewKindOf('clip.webm')).toBe('video');
    expect(viewKindOf('notes.md')).toBe('text');
    expect(viewKindOf('Makefile')).toBe('text');
  });

  it('shows HTML as text, never as a page', () => {
    expect(viewKindOf('index.html')).toBe('text');
    expect(viewKindOf('page.htm')).toBe('text');
  });

  it('opens nothing else', () => {
    expect(viewKindOf('archive.zip')).toBeNull();
    expect(viewKindOf('setup.exe')).toBeNull();
    expect(viewKindOf('photo.heic')).toBeNull();
    expect(viewKindOf('.hidden')).toBeNull();
  });
});

describe('hasThumbnail', () => {
  it('is true for the formats the daemon renders', () => {
    expect(hasThumbnail('a.jpeg')).toBe(true);
    expect(hasThumbnail('a.GIF')).toBe(true);
    expect(hasThumbnail('a.svg')).toBe(false);
    expect(hasThumbnail('a.heic')).toBe(false);
    expect(hasThumbnail('movie.mp4')).toBe(false);
  });
});
