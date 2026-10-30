import type { ErrorCode } from '@pithyx/api-client';
import type { PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';
import type { useTranslation } from 'react-i18next';
import type { DescribedError } from '../../errors/describe';
import { internalCall, type StorageDisk } from '../sdk/internal';

export type Translate = ReturnType<typeof useTranslation<'storage'>>['t'];

/** Every cached answer of the app; a change of the storage topics refreshes all of them. */
export const storageKey = ['storage'] as const;

/** A failed internal call as the error notice shows it. */
export function describeProblem(error: unknown): DescribedError {
  const problem = error as { code?: string; params?: Record<string, unknown>; requestId?: string };
  return {
    code: (problem.code ?? 'system.internal.unexpected') as ErrorCode,
    params: problem.params ?? {},
    requestId: problem.requestId ?? '',
  };
}

const HEALTHY_STATES = new Set(['ONLINE']);

export function stateTone(state: string): 'accent' | 'highlight' | 'danger' | 'neutral' {
  if (HEALTHY_STATES.has(state)) {
    return 'accent';
  }
  return state === 'DEGRADED' ? 'highlight' : state === 'UNKNOWN' ? 'neutral' : 'danger';
}

/** A state as the tone of a `StatusBadge` (D394): healthy green, degraded orange, failed red. */
export function stateStatus(state: string): 'success' | 'warning' | 'danger' | 'neutral' {
  const tone = stateTone(state);
  return tone === 'accent'
    ? 'success'
    : tone === 'highlight'
      ? 'warning'
      : tone === 'danger'
        ? 'danger'
        : 'neutral';
}

const STATES = [
  'online',
  'degraded',
  'faulted',
  'offline',
  'unavail',
  'removed',
  'suspended',
] as const;
type StateKey = (typeof STATES)[number] | 'unknown';

export function stateKey(state: string): StateKey {
  const key = state.toLowerCase();
  return STATES.find((known) => known === key) ?? 'unknown';
}

export function guardReason(t: Translate, guard: StorageDisk['guard']): string {
  return (t as unknown as (key: string, options: object) => string)(`reason.${guard.reason}`, {
    pool: guard.pool ?? '',
    contents: guard.contents.join(', '),
  });
}

export function formatDate(value: string, locale: string): string {
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
    new Date(value),
  );
}

/** Pools with their datasets, as last read by the daemon. */
export function usePools(app: PithyxApp) {
  return useQuery({
    queryKey: [...storageKey, 'pools'],
    queryFn: () => internalCall(app, 'internal.admin.storage.pools', {}),
  });
}

/** Every disk with the guard's verdict. */
export function useDisks(app: PithyxApp) {
  return useQuery({
    queryKey: [...storageKey, 'disks'],
    queryFn: () => internalCall(app, 'internal.admin.storage.disks', {}),
  });
}

/** Alerts and SMART reports. */
export const healthKey = [...storageKey, 'health'] as const;

export function useHealth(app: PithyxApp) {
  return useQuery({
    queryKey: healthKey,
    queryFn: () => internalCall(app, 'internal.admin.storage.health', {}),
  });
}

/** A dataset name or path that wraps after its slashes rather than inside a word. */
export function breakable(path: string): ReactNode[] {
  return path
    .split('/')
    .flatMap((part, index) =>
      index === 0 ? [part] : ['/', createElement('wbr', { key: index }), part],
    );
}
