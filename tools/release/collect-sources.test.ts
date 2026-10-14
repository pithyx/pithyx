import { describe, expect, it } from 'vitest';
import {
  groupSources,
  lookupName,
  parseBuiltUsing,
  parsePackages,
  planParts,
  sourceFiles,
  sourceOf,
} from './collect-sources';

describe('collect-sources', () => {
  it('reads the package lists of image/build.sh', () => {
    expect(parsePackages('acl\t2.3.2-2+b1\tamd64\n\nadduser\t3.152\tall\n')).toEqual([
      { name: 'acl', version: '2.3.2-2+b1' },
      { name: 'adduser', version: '3.152' },
    ]);
    expect(() => parsePackages('acl\n')).toThrow('not a package line');
  });

  it("skips Pithyx's own packages and finds the ZFS module's source through zfs-dkms", () => {
    expect(lookupName({ name: 'pithyx', version: '1.0.0~beta.1' })).toBeNull();
    expect(lookupName({ name: 'pithyx-kernel', version: '1.0.0~beta.1' })).toBeNull();
    expect(
      lookupName({ name: 'pithyx-zfs-modules-6.12.111+deb13-amd64', version: '2.3.9-0+deb13u1' }),
    ).toEqual({
      name: 'zfs-dkms',
      version: '2.3.9-0+deb13u1',
    });
    expect(lookupName({ name: 'acl', version: '2.3.2-2+b1' })).toEqual({
      name: 'acl',
      version: '2.3.2-2+b1',
    });
  });

  it('maps a binNMU binary version to the source version that built it', () => {
    const versions = [
      { binary_version: '2.3.2-2+b2', source: 'acl', version: '2.3.2-2' },
      { binary_version: '2.3.2-2+b1', source: 'acl', version: '2.3.2-2' },
    ];
    expect(sourceOf({ name: 'acl', version: '2.3.2-2+b1' }, versions)).toEqual({
      name: 'acl',
      version: '2.3.2-2',
    });
    expect(() => sourceOf({ name: 'acl', version: '9.9' }, versions)).toThrow('knows no acl 9.9');
  });

  it('lists each source once with the binaries it covers', () => {
    expect(
      groupSources([
        { binary: 'libacl1', source: { name: 'acl', version: '2.3.2-2' } },
        { binary: 'acl', source: { name: 'acl', version: '2.3.2-2' } },
        { binary: 'adduser', source: { name: 'adduser', version: '3.152' } },
      ]),
    ).toEqual([
      { name: 'acl', version: '2.3.2-2', binaries: ['acl', 'libacl1'] },
      { name: 'adduser', version: '3.152', binaries: ['adduser'] },
    ]);
  });

  it('takes the files of the debian archives, not of debian-debug only', () => {
    expect(
      sourceFiles({
        aaa: [
          { archive_name: 'debian-debug', name: 'acl_2.3.2.orig.tar.xz' },
          { archive_name: 'debian', name: 'acl_2.3.2.orig.tar.xz' },
        ],
        bbb: [{ archive_name: 'debian-security', name: 'acl_2.3.2-2.dsc' }],
        ccc: [{ archive_name: 'debian-debug', name: 'only-debug.deb' }],
      }),
    ).toEqual([
      { name: 'acl_2.3.2-2.dsc', sha1: 'bbb' },
      { name: 'acl_2.3.2.orig.tar.xz', sha1: 'aaa' },
    ]);
    expect(() => sourceFiles({})).toThrow('no files');
  });

  it('adds the sources a package was built with, like the kernel behind the signed image', () => {
    expect(
      parseBuiltUsing(
        'linux-image-6.12.111+deb13-amd64\tlinux (= 6.12.111-1)\ndocker.io\tgolang-1.24 (= 1.24.4-1), runc (= 1.3.0+ds1-1)\n',
      ),
    ).toEqual([
      {
        binary: 'linux-image-6.12.111+deb13-amd64',
        source: { name: 'linux', version: '6.12.111-1' },
      },
      { binary: 'docker.io', source: { name: 'golang-1.24', version: '1.24.4-1' } },
      { binary: 'docker.io', source: { name: 'runc', version: '1.3.0+ds1-1' } },
    ]);
    expect(() => parseBuiltUsing('x\tlinux (>= 6)\n')).toThrow('not a Built-Using entry of x');
  });

  it('splits the folders into release parts below the size limit', () => {
    const folders = [
      { name: 'a', bytes: 60 },
      { name: 'b', bytes: 30 },
      { name: 'c', bytes: 20 },
      { name: 'linux', bytes: 150 },
      { name: 'd', bytes: 10 },
    ];
    expect(planParts(folders, 100)).toEqual([['a', 'b'], ['c'], ['linux'], ['d']]);
  });
});
