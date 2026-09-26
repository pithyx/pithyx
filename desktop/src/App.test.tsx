import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { App } from './App';

describe('App', () => {
  it('renders the empty desktop surface', () => {
    expect(renderToStaticMarkup(<App />)).toBe('<main class="desktop-surface"></main>');
  });
});
