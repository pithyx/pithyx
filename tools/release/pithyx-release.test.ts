import { describe, expect, it } from 'vitest';
import { baselineFor, checklist, plan, PlanError, quote, type Facts } from './pithyx-release.ts';

const facts: Facts = {
  root: '/repo',
  productVersion: '1.0.0-beta.2',
  sdkVersion: '1.0.0-rc.9',
  builtVersion: '1.0.0-beta.2',
  out: [
    'pithyx-1.0.0-beta.2-amd64.iso',
    'pithyx-1.0.0-beta.2-amd64.packages',
    'pithyx-1.0.0-beta.1-amd64.packages',
  ],
  baselines: ['pithyx-1.0.0-beta.1-amd64.packages'],
  betaKey: '/home/owner/.config/pithyx-dev/pithyx-beta-1.pem',
  branch: 'dev',
  clean: true,
  tags: ['v1.0.0-beta.1', 'sdk-v1.0.0-rc.8'],
};

const commands = (steps: ReturnType<typeof plan>) =>
  steps.map((step) => [step.command, ...step.args].join(' '));

describe('the release plan', () => {
  it('builds an update of the version against the baseline and signs it with the beta key', () => {
    expect(commands(plan('update', [], { sign: true }, facts))).toEqual([
      '/repo/tools/release/build-update.sh --version 1.0.0-beta.2 --channel beta --since /repo/image/baseline/pithyx-1.0.0-beta.1-amd64.packages',
      '/repo/tools/release/sign-update.sh /repo/image/out/pithyx-1.0.0-beta.2-amd64.pithyxupdate --key /home/owner/.config/pithyx-dev/pithyx-beta-1.pem --key-id pithyx-beta-1',
    ]);
  });

  it('refuses an update whose packages are not built, unless it builds the image first', () => {
    expect(() => plan('update', [], { version: '1.0.0-beta.3' }, facts)).toThrow(PlanError);
    expect(() => plan('update', [], { version: '1.0.0-beta.3' }, facts)).toThrow(/--build-image/);
    expect(
      commands(plan('update', [], { version: '1.0.0-beta.3', 'build-image': true }, facts))[0],
    ).toBe('/repo/image/build.sh --version 1.0.0-beta.3');
  });

  it('finds the baseline in image/baseline, then in image/out, and says what to do without one', () => {
    expect(baselineFor({}, facts)).toBe('/repo/image/baseline/pithyx-1.0.0-beta.1-amd64.packages');
    expect(baselineFor({ minimum: '1.0.0-beta.2' }, facts)).toBe(
      '/repo/image/out/pithyx-1.0.0-beta.2-amd64.packages',
    );
    expect(() => baselineFor({ minimum: '0.9.0' }, facts)).toThrow(
      /pithyx-release baseline --version 0.9.0/,
    );
    expect(commands(plan('update', [], { full: true }, facts))[0]).toContain('--full');
  });

  it('needs the release key for a stable file and both key options together', () => {
    expect(() =>
      plan('update', [], { version: '1.0.0-beta.2', channel: 'stable', sign: true }, facts),
    ).toThrow(/release key/);
    expect(() => plan('sign', ['f.pithyxupdate'], { key: 'k.pem' }, facts)).toThrow(/go together/);
    expect(() => plan('sign', ['f'], {}, { ...facts, betaKey: null })).toThrow(/no beta key/);
    expect(() => plan('iso', [], { version: 'one' }, facts)).toThrow(/not a version/);
  });

  it('moves latest of every npm package only when asked and never in a dry run of npm', () => {
    expect(commands(plan('publish-sdk', [], { latest: true, otp: '123456' }, facts))).toEqual([
      '/repo/tools/release/publish-sdk.sh --otp 123456',
      'npm dist-tag add @pithyx/sdk@1.0.0-rc.9 latest --otp 123456',
      'npm dist-tag add @pithyx/ui@1.0.0-rc.9 latest --otp 123456',
      'npm dist-tag add @pithyx/cli@1.0.0-rc.9 latest --otp 123456',
    ]);
    expect(commands(plan('publish-sdk', [], { latest: true, 'npm-dry-run': true }, facts))).toEqual(
      ['/repo/tools/release/publish-sdk.sh --dry-run'],
    );
  });

  it('keeps a baseline only of an image that was built', () => {
    expect(commands(plan('baseline', [], {}, facts))).toEqual([
      'cp /repo/image/out/pithyx-1.0.0-beta.2-amd64.packages /repo/image/baseline/pithyx-1.0.0-beta.2-amd64.packages',
    ]);
    expect(() => plan('baseline', [], { version: '1.0.0' }, facts)).toThrow(/build the image/);
  });

  it('marks the release steps this machine shows done', () => {
    const lines = checklist('1.0.0-beta.2', facts);
    expect(lines[0]).toMatch(/^\[x\] daemon/);
    expect(lines[1]).toMatch(/^\[x\] ISO/);
    expect(lines[2]).toMatch(/^\[ \] GPL sources/);
    expect(lines[3]).toMatch(/^\[ \] baseline/);
    expect(lines[4]).toMatch(/^\[ \] tag v1.0.0-beta.2/);
  });

  it('quotes only what the shell would split', () => {
    expect(quote('--version')).toBe('--version');
    expect(quote("it's mine")).toBe(`'it'\\''s mine'`);
  });
});
