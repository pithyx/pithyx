import type { FileOperation, JobInfo } from '@vyxis/sdk';
import { create } from 'zustand';

export interface Upload {
  id: string;
  name: string;
  loaded: number;
  total: number;
}

export interface RunningJob {
  job: JobInfo;
  /** What was started, so a name conflict can be asked about and started again. */
  operation: FileOperation | null;
}

interface TransfersState {
  uploads: Record<string, Upload>;
  jobs: Record<number, RunningJob>;
}

/** Uploads and jobs of one Files window. Progress changes often, so it lives outside React state. */
export function createTransfers() {
  const store = create<TransfersState>(() => ({ uploads: {}, jobs: {} }));
  return {
    useStore: store,
    addUpload(upload: Upload) {
      store.setState((state) => ({ uploads: { ...state.uploads, [upload.id]: upload } }));
    },
    progress(id: string, loaded: number) {
      store.setState((state) => {
        const upload = state.uploads[id];
        return upload === undefined
          ? state
          : { uploads: { ...state.uploads, [id]: { ...upload, loaded } } };
      });
    },
    finishUpload(id: string) {
      store.setState((state) => {
        const uploads = withoutKey(state.uploads, id);
        return { uploads };
      });
    },
    addJob(job: JobInfo, operation: FileOperation | null) {
      store.setState((state) => ({ jobs: { ...state.jobs, [job.id]: { job, operation } } }));
    },
    /** Takes a job's new state; returns what was started when it just finished. */
    updateJob(job: JobInfo): RunningJob | undefined {
      const running = store.getState().jobs[job.id];
      if (running === undefined) {
        return undefined;
      }
      store.setState((state) => {
        if (FINISHED.has(job.state)) {
          const jobs = withoutKey(state.jobs, job.id);
          return { jobs };
        }
        return { jobs: { ...state.jobs, [job.id]: { ...running, job } } };
      });
      return FINISHED.has(job.state) ? { ...running, job } : undefined;
    },
  };
}

export type Transfers = ReturnType<typeof createTransfers>;

const FINISHED = new Set<JobInfo['state']>(['succeeded', 'failed', 'cancelled', 'interrupted']);

function withoutKey<K extends string | number, T>(record: Record<K, T>, key: K): Record<K, T> {
  return Object.fromEntries(
    Object.entries(record).filter(([name]) => name !== String(key)),
  ) as Record<K, T>;
}
