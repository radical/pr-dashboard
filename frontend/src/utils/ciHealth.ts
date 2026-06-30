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

// Triggers an on-demand Copilot triage of the currently-failing lanes (auth-gated). This shells out to
// the Copilot CLI on the server and can take a few minutes, so callers should expect a long wait.
export async function runCiTriage(signal?: AbortSignal): Promise<CiHealthResponse> {
  const response = await fetch('/api/ci-health/triage', { method: 'POST', signal });
  if (response.status === 401) {
    throw new Error('Sign in with GitHub to run triage.');
  }
  if (response.status === 503) {
    throw new Error('CI triage is disabled on this server.');
  }
  return readJson<CiHealthResponse>(response);
}
