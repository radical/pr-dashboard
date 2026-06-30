// Shared formatting helpers for the CI health and Bots pages, which render off the same
// /api/ci-health snapshot.

export function percent(value: number): string {
  return `${Math.round(value * 100)}%`;
}

export function delta(value: number): string {
  return value >= 0 ? `▲ +${percent(value)}` : `▼ ${percent(Math.abs(value))}`;
}

export function relativeTime(iso: string): string {
  const minutes = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

// Drops the owner prefix ("microsoft/aspire" -> "aspire") for readability, but only when the bare repo
// name is unique among the repos on screen — so e.g. microsoft/aspire vs CommunityToolkit/Aspire keep
// their owners and don't collide.
export function buildRepoLabeler(repos: Iterable<string>): (repo: string) => string {
  const ownersByName = new Map<string, Set<string>>();
  for (const repo of repos) {
    const slash = repo.indexOf('/');
    if (slash < 0) continue;
    const name = repo.slice(slash + 1).toLowerCase();
    const owner = repo.slice(0, slash);
    let owners = ownersByName.get(name);
    if (!owners) {
      owners = new Set();
      ownersByName.set(name, owners);
    }
    owners.add(owner);
  }

  return (repo: string) => {
    const slash = repo.indexOf('/');
    if (slash < 0) return repo;
    const name = repo.slice(slash + 1);
    return (ownersByName.get(name.toLowerCase())?.size ?? 0) > 1 ? repo : name;
  };
}
