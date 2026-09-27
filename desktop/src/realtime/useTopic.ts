import type { TopicName, TopicSnapshots } from '@pithyx/api-client';
import { useEffect } from 'react';
import { realtime, useRealtime } from './connection';

/** The live state of a topic; undefined until the first snapshot arrives. */
export function useTopic<K extends TopicName>(topic: K): TopicSnapshots[K] | undefined {
  // Subscribing is synchronization with the WebSocket, not data fetching.
  useEffect(() => realtime.subscribe(topic), [topic]);
  return useRealtime((state) => state.data[topic]);
}
