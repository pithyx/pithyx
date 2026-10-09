import { describe, expect, it } from 'vitest';
import {
  bondCandidates,
  defaultInterface,
  gatewayCandidates,
  hostAfterChange,
  ipFormOf,
  sameConfig,
  secondsLeft,
  splitList,
  urlFor,
  vlanName,
  vlanParents,
  withInterface,
  withIp,
  withoutInterface,
  type NetworkConfig,
  type NetworkLink,
} from './model';

const card = (name: string): NetworkLink => ({
  name,
  kind: 'ethernet',
  macAddress: null,
  up: true,
  carrier: true,
  speedMbps: 1000,
  duplex: 'full',
  mtu: 1500,
  addresses: [],
  gateways: [],
  dnsServers: [],
  defaultRoute: false,
  master: null,
  vlanParent: null,
  vlanId: null,
  method: 'dhcp',
  configured: false,
  changeable: true,
});

const links = [
  card('enp1s0'),
  card('enp2s0'),
  card('enp3s0'),
  { ...card('ens18'), changeable: false },
];
const empty: NetworkConfig = {
  interfaces: [],
  global: { hostName: 'nas', dnsServers: [], searchDomains: [] },
};
const bond = {
  ...defaultInterface('bond0'),
  kind: 'bond' as const,
  bond: { members: ['enp2s0', 'enp3s0'], mode: 'activeBackup' as const },
};

describe('network model', () => {
  it('sends addresses only for static modes', () => {
    const form = {
      ...ipFormOf(undefined),
      ipv4Address: ' 10.0.0.5/24 ',
      ipv6: 'static' as const,
      ipv6Address: 'fd00::5/64',
    };
    expect(withIp(defaultInterface('enp1s0'), form)).toMatchObject({
      ipv4: 'dhcp',
      ipv4Address: null,
      ipv6: 'static',
      ipv6Address: 'fd00::5/64',
      ipv6Gateway: null,
    });
    expect(withIp(defaultInterface('enp1s0'), { ...form, ipv4: 'static' }).ipv4Address).toBe(
      '10.0.0.5/24',
    );
  });

  it('links to the box on another address with the same scheme and port', () => {
    expect(urlFor('192.168.1.10', { protocol: 'http:', port: '7484' })).toBe(
      'http://192.168.1.10:7484/',
    );
    expect(urlFor('2001:db8::10', { protocol: 'https:', port: '' })).toBe(
      'https://[2001:db8::10]/',
    );
  });

  it('counts down whole seconds and stops at zero', () => {
    expect(secondsLeft('2026-10-09T12:01:00Z', Date.parse('2026-10-09T12:00:00.500Z'))).toBe(60);
    expect(secondsLeft('2026-10-09T12:01:00Z', Date.parse('2026-10-09T12:02:00Z'))).toBe(0);
    expect(splitList(' 9.9.9.9, 2620:fe::fe  1.1.1.1')).toEqual([
      '9.9.9.9',
      '2620:fe::fe',
      '1.1.1.1',
    ]);
  });

  it('offers only free cards for a bond and only cards outside bonds as VLAN parents', () => {
    const config = withInterface(withInterface(empty, bond), defaultInterface('enp1s0'));
    expect(bondCandidates(links, config, 'bond0')).toEqual(['enp2s0', 'enp3s0']);
    expect(bondCandidates(links, config, 'bond1')).toEqual([]);
    expect(vlanParents(links, config)).toEqual(['bond0', 'enp1s0']);
  });

  it('takes VLANs and the default route with a removed interface', () => {
    const vlan = {
      ...defaultInterface('bond0.7'),
      kind: 'vlan' as const,
      vlan: { parent: 'bond0', id: 7 },
    };
    const config = {
      ...withInterface(withInterface(empty, bond), vlan),
      global: { ...empty.global, defaultInterface: 'bond0' },
    };
    expect(gatewayCandidates(config)).toEqual(['bond0', 'bond0.7']);
    const without = withoutInterface(config, 'bond0');
    expect(without.interfaces).toEqual([]);
    expect(without.global.defaultInterface).toBeNull();
  });

  it('names VLANs after their parent while the name fits', () => {
    expect(vlanName('enp1s0', 42)).toBe('enp1s0.42');
    expect(vlanName('enp1s0f0np0', 4094)).toBe('vlan4094');
  });

  it('compares configurations by value', () => {
    const stored = withInterface(empty, {
      ...bond,
      bond: { ...bond.bond, members: ['enp3s0', 'enp2s0'], lacpRate: 'slow', primary: null },
    });
    expect(sameConfig(stored, withInterface(empty, bond))).toBe(true);
    expect(sameConfig(stored, withInterface(empty, { ...bond, mtu: 9000 }))).toBe(false);
  });

  it('follows the connection to the new static address of its card', () => {
    const reached = [
      { ...card('enp1s0'), addresses: ['192.168.1.20/24', 'fd00::20/64'] },
      card('enp2s0'),
    ];
    const config = withInterface(empty, {
      ...defaultInterface('enp1s0'),
      ipv4: 'static',
      ipv4Address: '192.168.1.50/24',
    });
    expect(hostAfterChange(reached, config, '192.168.1.20')).toBe('192.168.1.50');
    expect(hostAfterChange(reached, config, '[fd00::20]')).toBe('[fd00::20]');
    expect(hostAfterChange(reached, empty, '192.168.1.20')).toBe('192.168.1.20');
  });
});
