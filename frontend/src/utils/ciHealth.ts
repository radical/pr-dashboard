import type { CiHealthResponse } from '../types';
import { readJson } from './http';

export async function fetchCiHealth(signal?: AbortSignal): Promise<CiHealthResponse> {
  const response = await fetch('/api/ci-health', { signal });
  return readJson<CiHealthResponse>(response);
}
