import { create } from 'zustand';
import { describeError, type DescribedError } from './describe';

interface ErrorState {
  current: DescribedError | null;
}

/** Errors that do not belong to a form are shown in one dialog. */
export const useErrorStore = create<ErrorState>(() => ({ current: null }));

export function showError(error: unknown): void {
  useErrorStore.setState({ current: describeError(error) });
}

export function closeError(): void {
  useErrorStore.setState({ current: null });
}
