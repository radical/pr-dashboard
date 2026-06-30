import { useCallback, useEffect, useState } from 'react';
import type { CiHealthResponse } from '../../types';
import { fetchCiHealth, refreshCiHealth, runBotShepherd, runCiTriage } from '../../utils/ciHealth';

// Shared state for the CI health and Bots pages: initial load of the cached snapshot plus on-demand
// "refresh now" (re-runs the server pulse), Copilot triage, and Copilot bot shepherd.
export function useCiHealth() {
  const [data, setData] = useState<CiHealthResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const [triaging, setTriaging] = useState(false);
  const [triageError, setTriageError] = useState<string | null>(null);
  const [shepherding, setShepherding] = useState(false);
  const [shepherdError, setShepherdError] = useState<string | null>(null);

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

  const triage = useCallback(async () => {
    setTriaging(true);
    setTriageError(null);
    try {
      setData(await runCiTriage());
    } catch (err: unknown) {
      setTriageError(err instanceof Error ? err.message : 'Triage failed');
    } finally {
      setTriaging(false);
    }
  }, []);

  const shepherd = useCallback(async () => {
    setShepherding(true);
    setShepherdError(null);
    try {
      setData(await runBotShepherd());
    } catch (err: unknown) {
      setShepherdError(err instanceof Error ? err.message : 'Shepherd failed');
    } finally {
      setShepherding(false);
    }
  }, []);

  return {
    data,
    error,
    refreshing,
    refreshError,
    refresh,
    triaging,
    triageError,
    triage,
    shepherding,
    shepherdError,
    shepherd,
  };
}
