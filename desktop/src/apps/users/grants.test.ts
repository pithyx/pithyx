import { describe, expect, it } from 'vitest';
import { allows, givenBy, shareAccess, withAllowed, withShareAccess } from './grants';

describe('grant editing', () => {
  it('gives and takes rights without touching the others', () => {
    const grants = withAllowed([{ resource: 'protocol:smb', level: 'allow' }], 'right:power', true);
    expect(allows(grants, 'right:power')).toBe(true);
    expect(allows(grants, 'protocol:smb')).toBe(true);
    const taken = withAllowed(grants, 'protocol:smb', false);
    expect(taken).toEqual([{ resource: 'right:power', level: 'allow' }]);
    expect(withAllowed(taken, 'right:power', true)).toHaveLength(1);
  });

  it('sets the access to a shared folder and removes it with none', () => {
    const read = withShareAccess([], 7, 'read');
    expect(shareAccess(read, 7)).toBe('read');
    expect(shareAccess(read, 8)).toBe('none');
    const write = withShareAccess(read, 7, 'readWrite');
    expect(write).toEqual([{ resource: 'share:7', level: 'readWrite' }]);
    expect(withShareAccess(write, 7, 'none')).toEqual([]);
  });

  it('names each group that gives a resource once', () => {
    expect(
      givenBy(
        [
          { resource: 'protocol:smb', level: 'allow', groupId: 100, groupName: 'users' },
          { resource: 'protocol:smb', level: 'allow', groupId: 100, groupName: 'users' },
          { resource: 'protocol:ftp', level: 'allow', groupId: 30000, groupName: 'media' },
        ],
        'protocol:smb',
      ),
    ).toEqual(['users']);
  });
});
