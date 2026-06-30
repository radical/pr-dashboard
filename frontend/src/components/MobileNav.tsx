import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ReactNode } from 'react';
import type { AuthStatus, DashboardMode } from '../types';
import GitHubAvatar from './GitHubAvatar';
import NotificationSettings from './NotificationSettings';

type MobileNavProps = {
  isMobile: boolean;
  dashboardMode: DashboardMode;
  authStatus: AuthStatus | null;
  loginLoading: boolean;
  currentMilestoneLabel: string;
  onSwitchMode: (mode: DashboardMode) => void;
  onLogin: () => void;
  onLogout: () => void;
};

const MODES: { id: DashboardMode; label: string; icon: ReactNode }[] = [
  {
    id: 'review',
    label: 'Review mode',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7S1 12 1 12z" />
        <circle cx="12" cy="12" r="3" />
      </svg>
    ),
  },
  {
    id: 'issues',
    label: 'Issues mode',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
        <circle cx="12" cy="12" r="9" />
        <path d="M12 8v5M12 16h.01" />
      </svg>
    ),
  },
  {
    id: 'ship',
    label: 'Ship mode',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M3 13c3 0 3 2 6 2s3-2 6-2 3 2 6 2" />
        <path d="M5 13V7l7-4 7 4v6" />
      </svg>
    ),
  },
  { id: 'ci-health', label: 'CI health', icon: <span aria-hidden>🩺</span> },
];

function HamburgerIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
      <path d="M4 7h16M4 12h16M4 17h16" />
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
      <path d="M6 6l12 12M18 6 6 18" />
    </svg>
  );
}

function modeLabel(mode: DashboardMode): string {
  return mode === 'ship' ? 'Ship' : mode === 'issues' ? 'Issues' : mode === 'ci-health' ? 'CI health' : 'Review';
}

function MobileNav({
  isMobile,
  dashboardMode,
  authStatus,
  loginLoading,
  currentMilestoneLabel,
  onSwitchMode,
  onLogin,
  onLogout,
}: MobileNavProps) {
  const [open, setOpen] = useState(false);
  const drawerRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const drawerId = useId();

  const authenticated = authStatus?.authenticated === true;

  // Close + restore focus to the hamburger when the drawer is dismissed. The
  // focus restore is deferred a tick so it lands after React unmounts the
  // aria-modal dialog, rather than briefly moving focus behind a still-open modal.
  const dismiss = useCallback(() => {
    setOpen(false);
    requestAnimationFrame(() => triggerRef.current?.focus());
  }, []);

  // Lock body scroll, wire Escape, trap Tab focus inside the drawer, and move
  // focus into the drawer while open.
  useEffect(() => {
    if (!open) {
      return;
    }

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    const focusableSelector =
      'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

    const getFocusable = () =>
      Array.from(drawerRef.current?.querySelectorAll<HTMLElement>(focusableSelector) ?? []).filter(
        (el) => el.offsetParent !== null || el === document.activeElement,
      );

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        dismiss();
        return;
      }

      if (event.key !== 'Tab') {
        return;
      }

      // Keep Tab/Shift+Tab cycling within the drawer so focus can't reach the
      // obscured background content behind the modal overlay.
      const focusable = getFocusable();
      if (focusable.length === 0) {
        event.preventDefault();
        drawerRef.current?.focus();
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement as HTMLElement | null;
      const inDrawer = active ? drawerRef.current?.contains(active) === true : false;

      if (event.shiftKey) {
        if (!inDrawer || active === first) {
          event.preventDefault();
          last.focus();
        }
      } else if (!inDrawer || active === last) {
        event.preventDefault();
        first.focus();
      }
    };
    window.addEventListener('keydown', onKeyDown);

    // Focus the first focusable control in the drawer.
    const focusTarget = drawerRef.current?.querySelector<HTMLElement>(focusableSelector);
    focusTarget?.focus();

    return () => {
      document.body.style.overflow = previousOverflow;
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [open, dismiss]);

  // If the viewport grows back to desktop while the drawer is open, close it so
  // the body scroll lock doesn't linger.
  useEffect(() => {
    if (!isMobile && open) {
      setOpen(false);
    }
  }, [isMobile, open]);

  if (!isMobile) {
    return null;
  }

  const handleMode = (mode: DashboardMode) => {
    onSwitchMode(mode);
    dismiss();
  };

  return (
    <>
      <div className="mobile-appbar">
        <img className="mobile-appbar-logo" src="/aspire-logo-light-horizontal.svg" alt="Aspire" />
        <span className="mobile-appbar-mode">
          {modeLabel(dashboardMode)}
          {dashboardMode === 'ship' && currentMilestoneLabel ? ` · ${currentMilestoneLabel}` : ''}
        </span>
        <span className="mobile-appbar-spacer" />
        <button
          ref={triggerRef}
          type="button"
          className="mobile-appbar-burger"
          aria-haspopup="dialog"
          aria-expanded={open}
          aria-controls={open ? drawerId : undefined}
          aria-label="Open menu"
          onClick={() => setOpen(true)}
        >
          <HamburgerIcon />
        </button>
      </div>

      {open &&
        createPortal(
          <div className="mobile-drawer-root">
            <div className="mobile-drawer-overlay" onClick={dismiss} aria-hidden="true" />
            <div
              ref={drawerRef}
              id={drawerId}
              className="mobile-drawer"
              role="dialog"
              aria-modal="true"
              aria-label="Menu"
              tabIndex={-1}
            >
              <div className="mobile-drawer-head">
                <span className="mobile-drawer-title">Menu</span>
                <button
                  type="button"
                  className="mobile-drawer-close"
                  aria-label="Close menu"
                  onClick={dismiss}
                >
                  <CloseIcon />
                </button>
              </div>

              <div className="mobile-drawer-section">
                <span className="mobile-drawer-label">View</span>
                <div className="mobile-mode-list" role="group" aria-label="Dashboard mode">
                  {MODES.map((mode) => (
                    <button
                      key={mode.id}
                      type="button"
                      className={`mobile-mode-item${dashboardMode === mode.id ? ' active' : ''}`}
                      aria-pressed={dashboardMode === mode.id}
                      onClick={() => handleMode(mode.id)}
                    >
                      <span className="mobile-mode-icon">{mode.icon}</span>
                      {mode.label}
                    </button>
                  ))}
                </div>
              </div>

              <div className="mobile-drawer-section">
                <span className="mobile-drawer-label">Account</span>
                <div className="mobile-user">
                  {authenticated && authStatus?.login ? (
                    <>
                      <GitHubAvatar login={authStatus.login} className="mobile-user-avatar" size={48} />
                      <span className="mobile-user-who">
                        <strong>{authStatus.login}</strong>
                        <span className="mobile-user-status">Signed in</span>
                      </span>
                      <button type="button" className="mobile-user-action" onClick={onLogout}>
                        Sign out
                      </button>
                    </>
                  ) : (
                    <>
                      <span className="mobile-user-who">
                        <strong>{authStatus?.login ?? authStatus?.source ?? 'GitHub'}</strong>
                        <span className="mobile-user-status warning">Not signed in</span>
                      </span>
                      <button
                        type="button"
                        className="mobile-user-action"
                        onClick={onLogin}
                        disabled={!authStatus?.canLogin || loginLoading}
                      >
                        {loginLoading ? 'Starting…' : 'Sign in'}
                      </button>
                    </>
                  )}
                </div>
              </div>

              <div className="mobile-drawer-section">
                <span className="mobile-drawer-label">Notifications</span>
                <NotificationSettings authStatus={authStatus} variant="inline" />
              </div>
            </div>
          </div>,
          document.body,
        )}
    </>
  );
}

export default MobileNav;
