import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ReactNode } from 'react';
import type { AuthStatus } from '../types';
import {
  fetchVapidPublicKey,
  getPreferences,
  isIos,
  isMobileDevice,
  isPushSupported,
  isStandalone,
  savePreferences,
  sendTestNotification,
  subscribeToPush,
  syncSubscription,
  unsubscribeFromPush,
} from '../utils/notifications';
import type { NotificationPreferences } from '../utils/notifications';

type NotificationSettingsProps = {
  authStatus: AuthStatus | null;
  // 'bell' renders the desktop bell button + anchored popover. 'inline' renders
  // the settings body directly (used inside the mobile nav drawer) so nothing
  // gets clipped off-screen.
  variant?: 'bell' | 'inline';
};

type Status = { kind: 'idle' | 'success' | 'error'; text: string };

const idleStatus: Status = { kind: 'idle', text: '' };

function BellIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path
        fill="currentColor"
        d="M8 16a2 2 0 0 0 1.985-1.75h-3.97A2 2 0 0 0 8 16Zm5.5-5.06V7a5.5 5.5 0 0 0-4.5-5.41V1a1 1 0 0 0-2 0v.59A5.5 5.5 0 0 0 2.5 7v3.94l-1.2 1.2a.75.75 0 0 0 .53 1.28h12.34a.75.75 0 0 0 .53-1.28l-1.2-1.2Z"
      />
    </svg>
  );
}

function EyeIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path
        fill="currentColor"
        d="M8 3C4.5 3 1.73 5.11 1 8c.73 2.89 3.5 5 7 5s6.27-2.11 7-5c-.73-2.89-3.5-5-7-5Zm0 8a3 3 0 1 1 0-6 3 3 0 0 1 0 6Zm0-1.5A1.5 1.5 0 1 0 8 6.5a1.5 1.5 0 0 0 0 3Z"
      />
    </svg>
  );
}

function AtIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path
        fill="currentColor"
        d="M8 1a7 7 0 1 0 3.94 12.78.75.75 0 1 0-.84-1.24A5.5 5.5 0 1 1 13.5 8v.6a1 1 0 0 1-2 0V8a3.5 3.5 0 1 0-1.03 2.48A2.5 2.5 0 0 0 15 8.6V8a7 7 0 0 0-7-7Zm0 9a2 2 0 1 1 0-4 2 2 0 0 1 0 4Z"
      />
    </svg>
  );
}

function CheckXIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path
        fill="currentColor"
        d="M8 1a7 7 0 1 0 0 14A7 7 0 0 0 8 1Zm2.78 8.72a.75.75 0 1 1-1.06 1.06L8 9.06l-1.72 1.72a.75.75 0 0 1-1.06-1.06L6.94 8 5.22 6.28a.75.75 0 0 1 1.06-1.06L8 6.94l1.72-1.72a.75.75 0 1 1 1.06 1.06L9.06 8l1.72 1.72Z"
      />
    </svg>
  );
}

function MergeIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path
        fill="currentColor"
        d="M5 3.5a1.5 1.5 0 1 0-2 1.415v6.17a1.5 1.5 0 1 0 1 0V8.06A4.5 4.5 0 0 0 7.5 9.5h.585a1.5 1.5 0 1 0 0-1H7.5A3.5 3.5 0 0 1 4 5.06v-.145A1.5 1.5 0 0 0 5 3.5Z"
      />
    </svg>
  );
}

// The catalog of notification types. `reviewRequested` and `readyToMerge` are wired today;
// the rest are placeholders so the UI advertises what's coming and gives us a single place to
// add new types as the server learns to detect them.
type NotificationType = {
  id: string;
  title: string;
  description: string;
  icon: ReactNode;
  comingSoon?: boolean;
};

const NOTIFICATION_TYPES: NotificationType[] = [
  {
    id: 'reviewRequested',
    title: 'Review requested',
    description: 'When you’re added as a requested reviewer on an open PR.',
    icon: <EyeIcon />,
  },
  {
    id: 'readyToMerge',
    title: 'Ready to merge',
    description: 'When a PR you authored or approved is ready to merge.',
    icon: <MergeIcon />,
  },
  {
    id: 'buildBroken',
    title: 'Main build broken',
    description: 'When a main or release-branch build goes red at tip. Sent to everyone who turns this on.',
    icon: <CheckXIcon />,
  },
  {
    id: 'mentioned',
    title: 'Mentioned',
    description: 'When someone @mentions you in a PR or review.',
    icon: <AtIcon />,
    comingSoon: true,
  },
  {
    id: 'checksFailed',
    title: 'Checks failed',
    description: 'When required checks fail on a PR you own.',
    icon: <CheckXIcon />,
    comingSoon: true,
  },
];

type SwitchProps = {
  on: boolean;
  disabled?: boolean;
  label: string;
  onClick?: () => void;
};

function Switch({ on, disabled, label, onClick }: SwitchProps) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      aria-label={label}
      className={`notif-switch${on ? ' on' : ''}`}
      disabled={disabled}
      onClick={onClick}
    />
  );
}

// A cross-platform nudge shown on phones/tablets that aren't installed to the
// Home Screen yet. On iOS this is required for push to work at all; on Android
// it's recommended for a reliable, native-feeling experience. The "How?"
// disclosure keeps the steps for both platforms one tap away.
function InstallNote({ required }: { required?: boolean }) {
  return (
    <div className="notif-install">
      <div className="notif-install-head">
        <span className="notif-install-icon" aria-hidden="true">📲</span>
        <span className="notif-install-title">
          {required
            ? 'Install as an app to enable notifications'
            : 'Install as an app for reliable notifications'}
        </span>
      </div>
      <p className="notif-install-copy">
        {required
          ? 'On iPhone and iPad, push notifications only work when this dashboard is added to your Home Screen and opened from there.'
          : 'For reliable notifications, add this dashboard to your Home Screen, then open it from there.'}
      </p>
      <details className="notif-install-how">
        <summary>How?</summary>
        <div className="notif-install-platform">
          <span className="notif-install-platform-name">iPhone / iPad (Safari)</span>
          <ol>
            <li>Tap the Share button.</li>
            <li>Choose “Add to Home Screen”.</li>
            <li>Open the app from your Home Screen.</li>
          </ol>
        </div>
        <div className="notif-install-platform">
          <span className="notif-install-platform-name">Android (Chrome)</span>
          <ol>
            <li>Open the ⋮ menu.</li>
            <li>Tap “Install app” or “Add to Home screen”.</li>
            <li>Open the installed app.</li>
          </ol>
        </div>
      </details>
    </div>
  );
}

function NotificationSettings({ authStatus, variant = 'bell' }: NotificationSettingsProps) {
  const authenticated = authStatus?.authenticated === true;
  const login = authStatus?.login ?? '';

  const [supported] = useState(isPushSupported);
  const [iosNeedsInstall] = useState(() => isIos() && !isStandalone());
  // On Android (and other mobile) push can work in-browser, but we still nudge
  // toward installing for a consistent, reliable experience. Shown above the
  // working controls rather than replacing them.
  const [mobileNeedsInstall] = useState(() => isMobileDevice() && !isStandalone());
  const [serverKey, setServerKey] = useState<{ publicKey: string; keyId: string } | null>(null);
  const [serverChecked, setServerChecked] = useState(false);
  const [serverEnabled, setServerEnabled] = useState(false);
  const [permission, setPermission] = useState<NotificationPermission>(
    typeof Notification !== 'undefined' ? Notification.permission : 'default',
  );
  const [subscribed, setSubscribed] = useState(false);
  const [reviewRequested, setReviewRequested] = useState(true);
  const [readyToMerge, setReadyToMerge] = useState(true);
  const [buildBroken, setBuildBroken] = useState(false);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<Status>(idleStatus);

  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; right: number } | null>(null);
  const bellRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const popoverId = useId();

  // Require a real login before any push sync. Without it, syncSubscription(key, '') would look
  // like a switch to an empty account and could tear down an existing, valid subscription.
  const canUsePush = authenticated && supported && !iosNeedsInstall && login !== '';

  // A gentle nudge dot: push is available and configured but this device hasn't opted in yet.
  const showNudge =
    canUsePush && serverChecked && serverEnabled && !subscribed && permission !== 'denied';

  // Discover server config + current subscription state once the user is signed in.
  useEffect(() => {
    if (!canUsePush) {
      return;
    }

    let cancelled = false;

    void (async () => {
      try {
        const key = await fetchVapidPublicKey();
        if (cancelled) {
          return;
        }

        setServerChecked(true);
        if (!key) {
          setServerEnabled(false);
          return;
        }

        setServerEnabled(true);
        setServerKey(key);

        // Reconcile this device's subscription with the signed-in account. Returns false when
        // there's no subscription or when a different account previously owned this device (in
        // which case the stale subscription is torn down and an explicit opt-in is required).
        const synced = await syncSubscription(key, login);
        if (cancelled) {
          return;
        }

        if (synced) {
          setSubscribed(true);
          try {
            const prefs = await getPreferences();
            if (!cancelled) {
              setReviewRequested(prefs.reviewRequested);
              setReadyToMerge(prefs.readyToMerge);
              setBuildBroken(prefs.buildBroken);
            }
          } catch {
            // Non-fatal: leave the default toggle state.
          }
        } else {
          setSubscribed(false);
        }
      } catch {
        if (!cancelled) {
          setServerChecked(true);
          setServerEnabled(false);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [canUsePush, login]);

  // Position the portal popover under the bell and keep it anchored on scroll/resize.
  useEffect(() => {
    if (!open) {
      return;
    }

    const reposition = () => {
      const el = bellRef.current;
      if (!el) {
        return;
      }
      const rect = el.getBoundingClientRect();
      setPos({ top: rect.bottom + 8, right: Math.max(8, window.innerWidth - rect.right) });
    };

    reposition();
    window.addEventListener('resize', reposition);
    window.addEventListener('scroll', reposition, true);

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        bellRef.current?.focus();
      }
    };
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (popoverRef.current?.contains(target) || bellRef.current?.contains(target)) {
        return;
      }
      setOpen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('mousedown', onPointerDown);

    return () => {
      window.removeEventListener('resize', reposition);
      window.removeEventListener('scroll', reposition, true);
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('mousedown', onPointerDown);
    };
  }, [open]);

  // Clear transient status when the popover closes so it doesn't flash stale text on reopen.
  useEffect(() => {
    if (!open) {
      setStatus(idleStatus);
    }
  }, [open]);

  const enable = useCallback(async () => {
    if (!serverKey) {
      return;
    }

    setBusy(true);
    setStatus(idleStatus);
    try {
      const result = await Notification.requestPermission();
      setPermission(result);
      if (result !== 'granted') {
        setStatus({ kind: 'error', text: 'Notification permission was not granted.' });
        return;
      }

      await subscribeToPush(serverKey, login);
      setSubscribed(true);
      try {
        const prefs = await getPreferences();
        setReviewRequested(prefs.reviewRequested);
        setReadyToMerge(prefs.readyToMerge);
        setBuildBroken(prefs.buildBroken);
      } catch {
        // Ignore; defaults apply.
      }
      setStatus({ kind: 'success', text: 'Notifications enabled on this device.' });
    } catch (error) {
      setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not enable notifications.' });
    } finally {
      setBusy(false);
    }
  }, [serverKey, login]);

  const disable = useCallback(async () => {
    setBusy(true);
    setStatus(idleStatus);
    try {
      await unsubscribeFromPush();
      setSubscribed(false);
      setStatus({ kind: 'success', text: 'Notifications turned off on this device.' });
    } catch (error) {
      setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not turn off notifications.' });
    } finally {
      setBusy(false);
    }
  }, []);

  const persistPreferences = useCallback(
    async (next: NotificationPreferences, rollback: () => void) => {
      setBusy(true);
      setStatus(idleStatus);
      try {
        const saved = await savePreferences(next);
        setReviewRequested(saved.reviewRequested);
        setReadyToMerge(saved.readyToMerge);
        setBuildBroken(saved.buildBroken);
      } catch (error) {
        rollback();
        setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not save preference.' });
      } finally {
        setBusy(false);
      }
    },
    [],
  );

  const toggleReviewRequested = useCallback(() => {
    const next = !reviewRequested;
    setReviewRequested(next);
    void persistPreferences({ reviewRequested: next, readyToMerge, buildBroken }, () => setReviewRequested(!next));
  }, [reviewRequested, readyToMerge, buildBroken, persistPreferences]);

  const toggleReadyToMerge = useCallback(() => {
    const next = !readyToMerge;
    setReadyToMerge(next);
    void persistPreferences({ reviewRequested, readyToMerge: next, buildBroken }, () => setReadyToMerge(!next));
  }, [reviewRequested, readyToMerge, buildBroken, persistPreferences]);

  const toggleBuildBroken = useCallback(() => {
    const next = !buildBroken;
    setBuildBroken(next);
    void persistPreferences({ reviewRequested, readyToMerge, buildBroken: next }, () => setBuildBroken(!next));
  }, [reviewRequested, readyToMerge, buildBroken, persistPreferences]);

  const test = useCallback(async () => {
    setBusy(true);
    setStatus(idleStatus);
    try {
      const result = await sendTestNotification();
      setStatus(
        result.sent > 0
          ? { kind: 'success', text: 'Test notification sent.' }
          : { kind: 'error', text: 'No device received the test. Try re-enabling notifications.' },
      );
    } catch (error) {
      setStatus({ kind: 'error', text: error instanceof Error ? error.message : 'Could not send a test notification.' });
    } finally {
      setBusy(false);
    }
  }, []);

  const toggleMaster = useCallback(() => {
    if (subscribed) {
      void disable();
    } else {
      void enable();
    }
  }, [subscribed, disable, enable]);

  // The live preference wiring, keyed by notification-type id. Adding a future type means
  // adding an entry here and to NOTIFICATION_TYPES — no other UI changes required.
  const liveControls: Record<string, { checked: boolean; toggle: () => void }> = {
    reviewRequested: { checked: reviewRequested, toggle: toggleReviewRequested },
    readyToMerge: { checked: readyToMerge, toggle: toggleReadyToMerge },
    buildBroken: { checked: buildBroken, toggle: toggleBuildBroken },
  };

  let body: ReactNode;
  if (!authenticated) {
    body = <p className="notif-hint">Sign in to get notified when a pull request needs your review.</p>;
  } else if (!supported) {
    body = <p className="notif-hint">This browser doesn’t support push notifications.</p>;
  } else if (iosNeedsInstall) {
    body = <InstallNote required />;
  } else if (serverChecked && !serverEnabled) {
    body = <p className="notif-hint">Push notifications aren’t configured on this server yet.</p>;
  } else if (permission === 'denied') {
    body = (
      <p className="notif-hint">
        Notifications are blocked for this site. Allow them in your browser settings, then reload.
      </p>
    );
  } else {
    body = (
      <>
        {mobileNeedsInstall && <InstallNote />}
        <div className="notif-master">
          <span className="notif-master-label">
            <span className="notif-master-title">Enabled on this device</span>
            <span className="notif-master-sub">Push is delivered to this browser.</span>
          </span>
          <Switch
            on={subscribed}
            disabled={busy || !serverKey}
            label="Enable notifications on this device"
            onClick={toggleMaster}
          />
        </div>

        <ul className="notif-types">
          {NOTIFICATION_TYPES.map((type) => {
            const live = type.comingSoon ? undefined : liveControls[type.id];
            const checked = live ? live.checked : false;
            return (
              <li key={type.id} className={`notif-type${type.comingSoon ? ' soon' : ''}`}>
                <span className="notif-type-icon">{type.icon}</span>
                <span className="notif-type-body">
                  <span className="notif-type-title">
                    {type.title}
                    {type.comingSoon && <span className="notif-type-badge">Soon</span>}
                  </span>
                  <span className="notif-type-desc">{type.description}</span>
                </span>
                <Switch
                  on={checked}
                  disabled={type.comingSoon || !live || !subscribed || busy}
                  label={`${type.title} notifications`}
                  onClick={live ? live.toggle : undefined}
                />
              </li>
            );
          })}
        </ul>

        {subscribed && (
          <div className="notif-actions">
            <button type="button" onClick={() => void test()} disabled={busy}>
              Send test
            </button>
          </div>
        )}

        <p className="notif-footnote">Preferences follow your account · on/off is per device.</p>
      </>
    );
  }

  const statusLine = status.kind !== 'idle' && (
    <p className={`notif-status ${status.kind}`} role={status.kind === 'error' ? 'alert' : 'status'}>
      {status.text}
    </p>
  );

  if (variant === 'inline') {
    return (
      <div className="notif-inline">
        {body}
        {statusLine}
      </div>
    );
  }

  return (
    <div className="notif-bell-wrap">
      <button
        ref={bellRef}
        type="button"
        className={`notif-bell${open ? ' active' : ''}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? popoverId : undefined}
        aria-label="Notifications"
        onClick={() => setOpen((value) => !value)}
      >
        <BellIcon />
        {showNudge && <span className="notif-bell-dot" aria-hidden="true" />}
      </button>

      {open &&
        pos &&
        createPortal(
          <div
            ref={popoverRef}
            id={popoverId}
            className="notif-popover"
            role="dialog"
            aria-label="Notification settings"
            style={{ top: pos.top, right: pos.right }}
          >
            <div className="notif-pop-header">
              <span className="notif-pop-title">Notifications</span>
              {authenticated && authStatus?.login && (
                <span className="notif-pop-meta">{authStatus.login}</span>
              )}
            </div>
            {body}
            {statusLine}
          </div>,
          document.body,
        )}
    </div>
  );
}

export default NotificationSettings;
