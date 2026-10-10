import { useMemo, useState, useSyncExternalStore } from 'react';

export interface FaqItem {
  id: string;
  question: string;
  category: string;
  tags: string[];
  html: string;
  /** Question, tags and answer as lowercase plain text, for matching. */
  text: string;
}

interface Labels {
  search: string;
  all: string;
  empty: string;
  /** "{count} of {total} questions" */
  count: string;
}

function subscribeToHash(onChange: () => void): () => void {
  window.addEventListener('hashchange', onChange);
  return () => {
    window.removeEventListener('hashchange', onChange);
  };
}

function words(query: string): string[] {
  return query.toLowerCase().split(/\s+/).filter(Boolean);
}

// Filters the questions as you type: every word must appear in the question, its tags or its answer. All answers are
// in the HTML from the start, so the page works without JavaScript and the site search indexes them.
export function FaqSearch({
  items,
  categories,
  labels,
}: {
  items: FaqItem[];
  categories: { id: string; label: string }[];
  labels: Labels;
}) {
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState<string | undefined>(undefined);
  const [openId, setOpenId] = useState<string | undefined>(undefined);

  const linked = useSyncExternalStore(
    subscribeToHash,
    () => decodeURIComponent(location.hash.slice(1)),
    () => '',
  );

  const visible = useMemo(() => {
    const needles = words(query);
    return items.filter(
      (item) =>
        (category === undefined || item.category === category) &&
        needles.every((word) => item.text.includes(word)),
    );
  }, [items, query, category]);

  const used = categories.filter((entry) => items.some((item) => item.category === entry.id));

  return (
    <div className="px-faq not-content">
      <input
        type="search"
        className="px-faq-search"
        placeholder={labels.search}
        aria-label={labels.search}
        value={query}
        onChange={(event) => {
          setQuery(event.target.value);
        }}
      />
      <div className="px-faq-categories" role="group">
        {[{ id: undefined, label: labels.all }, ...used].map((entry) => (
          <button
            key={entry.id ?? 'all'}
            type="button"
            aria-pressed={category === entry.id}
            onClick={() => {
              setCategory(entry.id);
            }}
          >
            {entry.label}
          </button>
        ))}
      </div>
      <p className="px-faq-count" aria-live="polite">
        {labels.count
          .replace('{count}', String(visible.length))
          .replace('{total}', String(items.length))}
      </p>
      {visible.length === 0 && <p>{labels.empty}</p>}
      <div className="px-faq-list">
        {visible.map((item) => (
          <details
            key={item.id}
            id={item.id}
            open={query !== '' || openId === item.id || linked === item.id}
            onToggle={(event) => {
              if (event.currentTarget.open) {
                setOpenId(item.id);
                history.replaceState(null, '', `#${item.id}`);
              }
            }}
          >
            <summary>
              <h2>{item.question}</h2>
            </summary>
            <div
              className="px-faq-answer sl-markdown-content"
              dangerouslySetInnerHTML={{ __html: item.html }}
            />
          </details>
        ))}
      </div>
    </div>
  );
}
