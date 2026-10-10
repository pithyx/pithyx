import { icons } from './icons.ts';
import type { Landing } from './types.ts';

export function FeatureGrid({ features }: { features: Landing['features'] }) {
  return (
    <section className="px-section" aria-labelledby="px-features">
      <h2 id="px-features">{features.title}</h2>
      <p className="px-intro">{features.intro}</p>
      <ul className="px-feature-grid">
        {features.items.map((item) => {
          const Icon = icons[item.icon];
          const body = (
            <>
              <span className="px-feature-icon" aria-hidden="true">
                <Icon size={24} stroke={1.75} />
              </span>
              <h3>{item.title}</h3>
              <p>{item.text}</p>
            </>
          );
          return (
            <li key={item.title} className="px-card">
              {item.href ? (
                <a className="px-card-link" href={item.href}>
                  {body}
                </a>
              ) : (
                body
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
