import {
  currentRelease,
  defaultRepos,
  defaultShipWeekReleaseBranch,
  defaultShipWeekRepoInput,
} from '../constants';
import type { DashboardMode } from '../types';

export type ShipWeekRouteParams = {
  repositoryInput: string;
  milestoneInput: string;
  releaseBranchInput: string;
};

export function parseRepositories(value: string, fallbackRepositories = defaultRepos) {
  const repositories = value
    .split(/[,\s]+/)
    .map((repository) => repository.trim())
    .filter(Boolean);

  const uniqueRepositories = [...new Set(repositories)];
  return uniqueRepositories.length > 0 ? uniqueRepositories : fallbackRepositories;
}

const bucketHashPrefix = '#bucket/';

export function bucketRouteId(label: string) {
  return label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'bucket';
}

export function createBucketHash(bucketId: string) {
  return `${bucketHashPrefix}${encodeURIComponent(bucketId)}`;
}

export function createBucketUrl(bucketId: string) {
  return `${window.location.origin}${window.location.pathname}${window.location.search}${createBucketHash(bucketId)}`;
}

export function replaceBucketHistory(bucketId: string) {
  const hash = createBucketHash(bucketId);
  if (window.location.hash !== hash) {
    window.history.replaceState({ view: 'dashboard', bucketId }, '', hash);
  }
}

export function parseBucketHash(hash: string) {
  const match = /^#bucket\/([^/]+)$/.exec(hash);
  if (!match) {
    return null;
  }

  return {
    bucketId: decodeURIComponent(match[1]),
  };
}

export function pushDetailHistory(repository: string, number: number) {
  const hash = `#pr/${encodeURIComponent(repository)}/${number}`;
  if (window.location.hash !== hash) {
    window.history.pushState({ view: 'details', repository, number }, '', hash);
  }
}

export function parseDetailHash(hash: string) {
  const match = /^#pr\/([^/]+)\/(\d+)$/.exec(hash);
  if (!match) {
    return null;
  }

  return {
    repository: decodeURIComponent(match[1]),
    number: Number.parseInt(match[2], 10),
  };
}

export function parseDashboardMode(search: string): DashboardMode {
  const mode = new URLSearchParams(search).get('mode')?.toLowerCase();
  if (mode === 'ship' || mode === 'issues' || mode === 'ci-health') {
    return mode;
  }

  return 'review';
}

export function parseShipWeekRouteParams(search: string): ShipWeekRouteParams {
  const searchParams = new URLSearchParams(search);
  return normalizeShipWeekRouteParams({
    repositoryInput: searchParams.get('repos') ?? '',
    milestoneInput: searchParams.get('milestone') ?? '',
    releaseBranchInput: searchParams.get('releaseBranch') ?? '',
  });
}

export function normalizeShipWeekRouteParams(params: Partial<ShipWeekRouteParams>): ShipWeekRouteParams {
  return {
    repositoryInput: params.repositoryInput?.trim() || defaultShipWeekRepoInput,
    milestoneInput: params.milestoneInput?.trim() || currentRelease,
    releaseBranchInput: params.releaseBranchInput?.trim() || defaultShipWeekReleaseBranch,
  };
}

export function createShipWeekUrl(params: ShipWeekRouteParams) {
  const url = new URL(window.location.href);
  url.hash = '';
  applyDashboardModeSearchParams(url.searchParams, 'ship', params);
  return url.toString();
}

export function pushDashboardModeHistory(mode: DashboardMode, shipWeekParams?: ShipWeekRouteParams) {
  const url = new URL(window.location.href);
  url.hash = '';
  applyDashboardModeSearchParams(url.searchParams, mode, shipWeekParams);
  const nextUrl = `${url.pathname}${url.search}${url.hash}`;
  if (`${window.location.pathname}${window.location.search}${window.location.hash}` !== nextUrl) {
    window.history.pushState({ view: 'dashboard', mode, shipWeekParams }, '', nextUrl);
  }
}

function applyDashboardModeSearchParams(
  searchParams: URLSearchParams,
  mode: DashboardMode,
  shipWeekParams?: ShipWeekRouteParams,
) {
  searchParams.set('mode', mode);
  if (mode !== 'ship') {
    searchParams.delete('repos');
    searchParams.delete('milestone');
    searchParams.delete('releaseBranch');
    return;
  }

  const params = normalizeShipWeekRouteParams(shipWeekParams ?? {});
  searchParams.set('repos', params.repositoryInput);
  searchParams.set('milestone', params.milestoneInput);
  if (params.releaseBranchInput) {
    searchParams.set('releaseBranch', params.releaseBranchInput);
  } else {
    searchParams.delete('releaseBranch');
  }
}

export function shortRepoName(repository: string) {
  const parts = repository.split('/');
  return parts[parts.length - 1] ?? repository;
}
