import type { PithyxApp } from '@pithyx/sdk';
import { useState } from 'react';
import { AdvancedSection } from '../storage/AdvancedSection';
import { DatasetsSection } from '../storage/DatasetsSection';

/** Advanced storage tasks, with the datasets as a view of their own that opens from there. */
export function StorageAdvancedPage({ app }: { app: PithyxApp }) {
  const [datasets, setDatasets] = useState(false);
  return datasets ? (
    <DatasetsSection
      app={app}
      onBack={() => {
        setDatasets(false);
      }}
    />
  ) : (
    <AdvancedSection
      app={app}
      onDatasets={() => {
        setDatasets(true);
      }}
    />
  );
}
