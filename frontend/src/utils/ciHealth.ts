import type { CiHealthResponse } from '../types';
import { readJson } from './http';

export async function fetchCiHealth(signal?: AbortSignal): Promise<CiHealthResponse> {
  const response = await fetch('/api/ci-health', { signal });
  return readJson<CiHealthResponse>(response);
}

// Triggers an on-demand pulse refresh on the server (auth-gated) and returns the fresh snapshot.
export async function refreshCiHealth(signal?: AbortSignal): Promise<CiHealthResponse> {
  const response = await fetch('/api/ci-health/refresh', { method: 'POST', signal });
  if (response.status === 401) {
    throw new Error('Sign in with GitHub to refresh.');
  }
  return readJson<CiHealthResponse>(response);
}
