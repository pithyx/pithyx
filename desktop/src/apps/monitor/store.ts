import { createStore } from 'zustand/vanilla';
import type { ProcessList, ResourceSample, ResourceSnapshot } from '../sdk/monitor';
import { append, pointFromSample, pointsFromHistory, type Point } from './series';

export interface MonitorState {
  cpuCount: number | null;
  memoryTotalBytes: number | null;
  points: Point[];
  latest: ResourceSample | null;
  processes: ProcessList | null;
}

function isSnapshot(data: unknown): data is ResourceSnapshot {
  return typeof data === 'object' && data !== null && 'history' in data;
}

/**
 * The live state of one Resource Monitor window: one sample per second is high-frequency state, so it lives in a
 * store the charts read with narrow selectors, not in React state or context.
 */
export function createMonitorStore() {
  const store = createStore<MonitorState>(() => ({
    cpuCount: null,
    memoryTotalBytes: null,
    points: [],
    latest: null,
    processes: null,
  }));
  return {
    store,
    /** A `system/resources` message: the snapshot (history plus newest sample) or one sample. */
    receiveResources: (data: unknown) => {
      if (isSnapshot(data)) {
        const points = pointsFromHistory(data.history);
        store.setState({
          cpuCount: data.cpuCount,
          memoryTotalBytes: data.memoryTotalBytes,
          points: data.latest === null ? points : append(points, pointFromSample(data.latest)),
          latest: data.latest,
        });
        return;
      }
      const sample = data as ResourceSample;
      store.setState((state) => ({
        points: append(state.points, pointFromSample(sample)),
        latest: sample,
        cpuCount: state.cpuCount ?? sample.cpu.cores.length,
        memoryTotalBytes: state.memoryTotalBytes ?? sample.memory.totalBytes,
      }));
    },
    receiveProcesses: (data: unknown) => {
      store.setState({ processes: data as ProcessList });
    },
  };
}

export type MonitorStore = ReturnType<typeof createMonitorStore>;
