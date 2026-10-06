import { useState } from 'react';
import { Button } from '@pithyx/ui';
import { LogoMark, Wordmark, type LogoMotion } from '../ui/Logo';
import { labels } from './labels';
import styles from './LogoScreen.module.css';

const motions: LogoMotion[] = ['static', 'closed', 'hover', 'welcome', 'loop', 'loading'];
const sizes = [16, 26, 32, 48];

/** Every logo variant at review size, the small sizes and the word mark. */
export function LogoScreen() {
  const [replay, setReplay] = useState(0);
  return (
    <div className={styles.page}>
      <h1 className={styles.title}>{labels.logo.title}</h1>
      <p className={styles.hint}>{labels.logo.hint}</p>
      <ul className={styles.grid}>
        {motions.map((motion) => (
          <li key={motion} className={styles.card}>
            {motion === 'hover' ? (
              <button
                type="button"
                className={styles.target}
                aria-label={labels.logo.motions[motion]}
              >
                <LogoMark size={96} motion={motion} />
              </button>
            ) : (
              <LogoMark key={motion === 'welcome' ? replay : motion} size={96} motion={motion} />
            )}
            <span className={styles.name}>{labels.logo.motions[motion]}</span>
            <span className={styles.hint}>{labels.logo.uses[motion]}</span>
            {motion === 'welcome' && (
              <Button
                size="sm"
                onPress={() => {
                  setReplay((count) => count + 1);
                }}
              >
                {labels.replay}
              </Button>
            )}
          </li>
        ))}
      </ul>
      <h2 className={styles.heading}>{labels.logo.sizes}</h2>
      <div className={styles.row}>
        {sizes.map((size) => (
          <span key={size} className={styles.size}>
            <LogoMark size={size} motion="loop" />
            <span className={styles.hint}>{`${String(size)} px`}</span>
          </span>
        ))}
      </div>
      <h2 className={styles.heading}>{labels.logo.wordmark}</h2>
      <div className={styles.row}>
        <Wordmark name="Pithyx" mark="PIΘYX" />
        <Wordmark name="Pithyx" mark="PIΘYX" size="lg" />
      </div>
    </div>
  );
}
