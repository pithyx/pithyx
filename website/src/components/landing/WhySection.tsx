import type { Landing } from './types.ts';

export function WhySection({ why }: { why: Landing['why'] }) {
  return (
    <section className="px-section" aria-labelledby="px-why">
      <h2 id="px-why">{why.title}</h2>
      <p className="px-intro">{why.intro}</p>
      <ol className="px-why">
        {why.items.map((item, index) => (
          <li key={item.title}>
            <span className="px-why-number" aria-hidden="true">
              {String(index + 1).padStart(2, '0')}
            </span>
            <div>
              <h3>{item.title}</h3>
              <p>{item.text}</p>
            </div>
          </li>
        ))}
      </ol>
    </section>
  );
}
