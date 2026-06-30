import { useCallback, useEffect, useState } from 'react';
import type { CiHealthResponse } from '../../types';
import { fetchCiHealth, refreshCiHealth } from '../../utils/ciHealth';

// Shared state for the CI health and Bots pages: initial load of the cached snapshot plus an on-demand
// "refresh now" that re-runs the server pulse and swaps in the fresh snapshot.
export function useCiHealth() {
  const [data, setData] = useState<CiHealthResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchCiHealth(controller.signal)
      .then(setData)
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : 'Failed to load CI health');
        }
      });
    return () => controller.abort();
  }, []);

  const refresh = useCallback(async () => {
    setRefreshing(true);
    setRefreshError(null);
    try {
      setData(await refreshCiHealth());
    } catch (err: unknown) {
      setRefreshError(err instanceof Error ? err.message : 'Refresh failed');
    } finally {
      setRefreshing(false);
    }
  }, []);

  return { data, error, refreshing, refreshError, refresh };
}
