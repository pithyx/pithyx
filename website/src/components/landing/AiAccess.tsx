import { useState, useSyncExternalStore } from 'react';
import type { Landing } from './types.ts';

// Tells language models and their users where the plain Markdown lives. The copy button only appears where the
// Clipboard API exists (a secure context); the prompt stays selectable everywhere.
export function AiAccess({ ai, site }: { ai: Landing['ai']; site: string }) {
  const canCopy = useSyncExternalStore(
    () => () => undefined,
    () => 'clipboard' in navigator,
    () => false,
  );
  const [copied, setCopied] = useState(false);
  const prompt = ai.prompt.replaceAll('{site}', site);

  return (
    <section className="px-section px-ai" aria-labelledby="px-ai">
      <h2 id="px-ai">{ai.title}</h2>
      <p className="px-intro">{ai.intro}</p>
      <ol className="px-ai-levels">
        {ai.levels.map((level) => (
          <li key={level.path} className="px-card">
            <h3>{level.title}</h3>
            <p>{level.text}</p>
            <a href={level.path}>
              <code>
                {site}
                {level.path}
              </code>
            </a>
          </li>
        ))}
      </ol>
      <div className="px-ai-prompt">
        <span>{ai.promptLabel}</span>
        <pre>
          <code>{prompt}</code>
        </pre>
        {canCopy && (
          <button
            type="button"
            onClick={() => {
              void navigator.clipboard.writeText(prompt).then(() => {
                setCopied(true);
                setTimeout(() => {
                  setCopied(false);
                }, 2000);
              });
            }}
          >
            {copied ? ai.copied : ai.copy}
          </button>
        )}
      </div>
    </section>
  );
}
