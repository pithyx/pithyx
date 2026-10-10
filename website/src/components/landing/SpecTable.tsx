import type { Landing } from './types.ts';

export function SpecTable({ specs }: { specs: Landing['specs'] }) {
  return (
    <section className="px-section" aria-labelledby="px-specs">
      <h2 id="px-specs">{specs.title}</h2>
      <dl className="px-specs">
        {specs.rows.map((row) => (
          <div key={row.label}>
            <dt>{row.label}</dt>
            <dd>{row.value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}
