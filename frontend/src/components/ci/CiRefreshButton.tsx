type CiRefreshButtonProps = {
  refreshing: boolean;
  refreshError: string | null;
  onRefresh: () => void;
};

// "Refresh now" control shown in the CI health / Bots page strip. Surfaces the in-flight state and any
// refresh error (e.g. a 401 prompting sign-in) inline next to the button.
function CiRefreshButton({ refreshing, refreshError, onRefresh }: CiRefreshButtonProps) {
  return (
    <span className="ci-refresh-wrap">
      {refreshError ? <span className="ci-refresh-error">{refreshError}</span> : null}
      <button type="button" className="ci-refresh" onClick={onRefresh} disabled={refreshing}>
        {refreshing ? 'Refreshing…' : '↻ Refresh now'}
      </button>
    </span>
  );
}

export default CiRefreshButton;
