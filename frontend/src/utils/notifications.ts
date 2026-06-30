// Browser push subscription helpers. Kept free of React so the gating logic and the
// VAPID key conversion can be unit tested with Vitest.

export type VapidPublicKey = {
  publicKey: string;
  keyId: string;
};

export type NotificationPreferences = {
  reviewRequested: boolean;
  readyToMerge: boolean;
  buildBroken: boolean;
};

export type TestNotificationResult = {
  sent: number;
  failed: number;
  expired: number;
};

// True when the browser exposes the APIs required for Web Push.
export function isPushSupported(): boolean {
  return (
    typeof navigator !== 'undefined' &&
    'serviceWorker' in navigator &&
    typeof window !== 'undefined' &&
    'PushManager' in window &&
    'Notification' in window
  );
}

export function isIos(): boolean {
  if (typeof navigator === 'undefined') {
    return false;
  }

  const ua = navigator.userAgent;
  // iPadOS 13+ reports as Macintosh but is touch-capable; cover both.
  return /iphone|ipad|ipod/i.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
}

export function isAndroid(): boolean {
  if (typeof navigator === 'undefined') {
    return false;
  }

  return /android/i.test(navigator.userAgent);
}

// True for any touch phone/tablet where "install to Home Screen" is the path to a
// native-feeling, reliably-notifying app. Used to decide whether to surface the
// install nudge; on Android push can also work in-browser, but we still show it
// for consistent cross-platform messaging.
export function isMobileDevice(): boolean {
  return isIos() || isAndroid();
}

// iOS Safari only allows push when the PWA is installed to the Home Screen (standalone).
export function isStandalone(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  const navigatorStandalone = (navigator as Navigator & { standalone?: boolean }).standalone;
  return (
    window.matchMedia?.('(display-mode: standalone)').matches === true ||
    navigatorStandalone === true
  );
}

// The deep link the server embeds in a notification, mirrored here so a test can assert the
// two stay in sync with the app router (parseDetailHash).
export function buildPullRequestDeepLink(repository: string, number: number): string {
  return `/#pr/${encodeURIComponent(repository)}/${number}`;
}

// Convert a base64url VAPID public key into the Uint8Array the PushManager expects.
export function urlBase64ToUint8Array(base64String: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
  const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
  const rawData = atob(base64);
  const outputArray = new Uint8Array(new ArrayBuffer(rawData.length));
  for (let i = 0; i < rawData.length; i += 1) {
    outputArray[i] = rawData.charCodeAt(i);
  }
  return outputArray;
}

// Returns the server VAPID config, or null when push is disabled server-side (404).
export async function fetchVapidPublicKey(): Promise<VapidPublicKey | null> {
  const response = await fetch('/api/notifications/vapid-public-key');
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    throw new Error('Could not load push configuration.');
  }
  return (await response.json()) as VapidPublicKey;
}

export async function getPreferences(): Promise<NotificationPreferences> {
  const response = await fetch('/api/notifications/preferences');
  if (!response.ok) {
    throw new Error('Could not load notification preferences.');
  }
  return (await response.json()) as NotificationPreferences;
}

export async function savePreferences(
  preferences: NotificationPreferences,
): Promise<NotificationPreferences> {
  const response = await fetch('/api/notifications/preferences', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(preferences),
  });
  if (!response.ok) {
    throw new Error('Could not save notification preferences.');
  }
  return (await response.json()) as NotificationPreferences;
}

export async function getServiceWorkerRegistration(): Promise<ServiceWorkerRegistration | null> {
  if (!('serviceWorker' in navigator)) {
    return null;
  }
  return (await navigator.serviceWorker.ready) ?? null;
}

export async function getExistingSubscription(): Promise<PushSubscription | null> {
  const registration = await getServiceWorkerRegistration();
  if (!registration) {
    return null;
  }
  return registration.pushManager.getSubscription();
}

async function postSubscription(subscription: PushSubscription): Promise<void> {
  const response = await fetch('/api/notifications/subscribe', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(subscription),
  });
  if (!response.ok) {
    throw new Error('Could not register this device for notifications.');
  }
}

// The server VAPID key id active when this device last subscribed. Lets us detect a key
// rotation and re-subscribe, without recreating the subscription on every page load.
const KEY_ID_STORAGE_KEY = 'pr-focus.vapid-key-id';

function getStoredKeyId(): string | null {
  try {
    return window.localStorage.getItem(KEY_ID_STORAGE_KEY);
  } catch {
    return null;
  }
}

function setStoredKeyId(keyId: string): void {
  try {
    window.localStorage.setItem(KEY_ID_STORAGE_KEY, keyId);
  } catch {
    // Ignore storage failures (private mode, etc.); resync just runs more often.
  }
}

// The GitHub login that enabled push on this device. Used to detect an account switch on a
// shared browser so we can drop the previous user's subscription instead of silently
// delivering their review notifications to whoever signed in next.
const OWNER_STORAGE_KEY = 'pr-focus.subscription-owner';

function getStoredOwner(): string | null {
  try {
    return window.localStorage.getItem(OWNER_STORAGE_KEY);
  } catch {
    return null;
  }
}

function setStoredOwner(login: string): void {
  try {
    window.localStorage.setItem(OWNER_STORAGE_KEY, login);
  } catch {
    // Ignore storage failures; the server also reassigns the endpoint on subscribe.
  }
}

function clearStoredOwner(): void {
  try {
    window.localStorage.removeItem(OWNER_STORAGE_KEY);
  } catch {
    // Ignore storage failures.
  }
}

// Subscribe this browser and register the subscription with the server. Caller is responsible
// for having already obtained Notification permission from a user gesture.
export async function subscribeToPush(key: VapidPublicKey, owner: string): Promise<PushSubscription> {
  const registration = await getServiceWorkerRegistration();
  if (!registration) {
    throw new Error('The service worker is not ready yet. Reload and try again.');
  }

  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(key.publicKey),
  });

  await postSubscription(subscription);
  setStoredKeyId(key.keyId);
  setStoredOwner(owner);
  return subscription;
}

// Keep this device's subscription in sync with the server on load. If a different account now
// owns this device, drop the stale subscription (the previous user must not keep receiving
// pushes here). If the server rotated its VAPID key, recreate the subscription; otherwise just
// refresh the server record. Returns true when a subscription exists for the current user.
export async function syncSubscription(key: VapidPublicKey, owner: string): Promise<boolean> {
  const existing = await getExistingSubscription();
  if (!existing) {
    return false;
  }

  const storedOwner = getStoredOwner();
  if (storedOwner && storedOwner.toLowerCase() !== owner.toLowerCase()) {
    // Account switched on this shared device: tear down the previous user's subscription and
    // require an explicit opt-in before this account receives notifications.
    await unsubscribeFromPush();
    return false;
  }

  if (getStoredKeyId() !== key.keyId) {
    await existing.unsubscribe().catch(() => undefined);
    await subscribeToPush(key, owner);
    return true;
  }

  // Same key and owner: refresh the server-side record without disturbing the subscription.
  await postSubscription(existing).catch(() => undefined);
  setStoredKeyId(key.keyId);
  setStoredOwner(owner);
  return true;
}

export async function unsubscribeFromPush(): Promise<void> {
  clearStoredOwner();
  const subscription = await getExistingSubscription();
  if (!subscription) {
    return;
  }

  await fetch('/api/notifications/unsubscribe', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ endpoint: subscription.endpoint }),
  }).catch(() => undefined);

  await subscription.unsubscribe().catch(() => undefined);
}

export async function sendTestNotification(): Promise<TestNotificationResult> {
  const response = await fetch('/api/notifications/test', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({}),
  });

  if (response.status === 429) {
    throw new Error('Too many test notifications. Wait a few seconds and try again.');
  }
  if (!response.ok) {
    throw new Error('Could not send a test notification.');
  }
  return (await response.json()) as TestNotificationResult;
}
