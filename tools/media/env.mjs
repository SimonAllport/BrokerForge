// Where the recordings point and what they hide. The dev IDs come from ~/.brokerforge (outside the repo, CLAUDE.md);
// nothing here names a real environment, user or host in what ends up in a picture.
import { existsSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const here = dirname(fileURLToPath(import.meta.url));
export const repo = join(here, '..', '..');
export const out = join(here, 'out');
export const images = join(repo, 'docs', 'guide', 'images');
export const profiles = join(homedir(), '.brokerforge', 'media-profiles');

function ids(file) {
  const path = join(homedir(), '.brokerforge', file);
  if (!existsSync(path)) return {};
  return Object.fromEntries(readFileSync(path, 'utf8').split('\n')
    .map(l => /^\s*(?:export\s+)?([A-Z_][A-Z0-9_]*)=["']?([^"'\n]*)["']?/.exec(l)).filter(Boolean).map(m => [m[1], m[2]]));
}
const dev = { ...ids('spike-ids.env'), ...ids('dev-ids.env') };
export const org = process.env.DV_ORG || dev.DV_ORG;
export const hostUrl = process.env.DEV_HOST_URL || dev.DEV_HOST_URL;
/** The dev environment's ID: connect pages of connection designs are at {host}/connect/<environment>/<instance>. */
export const envId = process.env.ENVID || dev.ENVID;

/** Real names and URLs, and what pictures show instead. Longest first, so a host is replaced before its domain. */
export const placeholders = [
  [hostUrl, 'https://brokerforge.contoso.example'],
  [envId, '00000000-0000-0000-0000-000000000000'],
  // The Container Apps environment's domain, in the mocks' addresses (bf-dev-orders-mock.internal.<domain>).
  [hostUrl && new URL(hostUrl).host.split('.').slice(1).join('.'), 'contoso.example'],
  [new URL(org).host, 'contoso.crm.dynamics.com'],
  [new URL(org).host.split('.')[0], 'contoso'],
].filter(([real]) => real);

export const sanitise = text => placeholders.reduce((t, [real, shown]) => t.split(real).join(shown), text);
