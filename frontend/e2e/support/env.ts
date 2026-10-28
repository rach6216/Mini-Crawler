/** Target stack; defaults match `docker compose up` with no overrides. */
export const BASE_URL = process.env.E2E_BASE_URL ?? 'http://localhost:3000';

/** URLs as seen by the worker container (its TrustedHosts allows `fixture-site`). */
export const FIXTURE_SITE = 'http://fixture-site/';
export const SLOW_FIXTURE_SITE = 'http://fixture-site/slow/';
