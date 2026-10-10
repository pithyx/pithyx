import type { Landing } from './types.ts';

export function CallToAction({ cta }: { cta: Landing['cta'] }) {
  return (
    <section className="px-section px-cta" aria-labelledby="px-cta">
      <h2 id="px-cta">{cta.title}</h2>
      <p>{cta.text}</p>
      <div className="px-cta-actions">
        <a className="px-button px-button-primary" href={cta.primary.href}>
          {cta.primary.label}
        </a>
        <a className="px-button" href={cta.secondary.href}>
          {cta.secondary.label}
        </a>
      </div>
    </section>
  );
}
