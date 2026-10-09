import type { components } from '@pithyx/api-client';

export type NetworkConfig = components['schemas']['NetworkConfig'];
export type InterfaceConfig = components['schemas']['InterfaceConfig'];
export type GlobalNetworkConfig = components['schemas']['GlobalNetworkConfig'];
export type NetworkLink = components['schemas']['NetworkLink'];
export type Ipv4Mode = components['schemas']['Ipv4Mode'];
export type Ipv6Mode = components['schemas']['Ipv6Mode'];
export type BondMode = components['schemas']['BondMode'];

/** The address fields of an interface as the form edits them: text until they are sent. */
export interface IpForm {
  ipv4: Ipv4Mode;
  ipv4Address: string;
  ipv4Gateway: string;
  ipv6: Ipv6Mode;
  ipv6Address: string;
  ipv6Gateway: string;
}

export const MAX_NAME_LENGTH = 15;

/** The form of an interface's settings; without settings a card gets DHCP and automatic IPv6. */
export function ipFormOf(item: InterfaceConfig | undefined): IpForm {
  return {
    ipv4: item?.ipv4 ?? 'dhcp',
    ipv4Address: item?.ipv4Address ?? '',
    ipv4Gateway: item?.ipv4Gateway ?? '',
    ipv6: item?.ipv6 ?? 'auto',
    ipv6Address: item?.ipv6Address ?? '',
    ipv6Gateway: item?.ipv6Gateway ?? '',
  };
}

const blank = (value: string): string | null => (value.trim() === '' ? null : value.trim());

/** The interface with the form's addresses; addresses and gateways only for static modes. */
export function withIp(item: InterfaceConfig, form: IpForm): InterfaceConfig {
  return {
    ...item,
    ipv4: form.ipv4,
    ipv4Address: form.ipv4 === 'static' ? blank(form.ipv4Address) : null,
    ipv4Gateway: form.ipv4 === 'static' ? blank(form.ipv4Gateway) : null,
    ipv6: form.ipv6,
    ipv6Address: form.ipv6 === 'static' ? blank(form.ipv6Address) : null,
    ipv6Gateway: form.ipv6 === 'static' ? blank(form.ipv6Gateway) : null,
  };
}

/** A card's settings as Pithyx stores them when it has none: DHCP and automatic IPv6. */
export function defaultInterface(name: string): InterfaceConfig {
  return {
    name,
    kind: 'ethernet',
    enabled: true,
    ipv4: 'dhcp',
    ipv4Address: null,
    ipv4Gateway: null,
    ipv6: 'auto',
    ipv6Address: null,
    ipv6Gateway: null,
    mtu: null,
    bond: null,
    vlan: null,
  };
}

/** Whole seconds until the deadline, never below zero. */
export function secondsLeft(deadline: string, now: number): number {
  return Math.max(0, Math.ceil((Date.parse(deadline) - now) / 1000));
}

/** The address of this page on another address of the box: same scheme and port, IPv6 in brackets. */
export function urlFor(address: string, location: { protocol: string; port: string }): string {
  const host = address.includes(':') ? `[${address}]` : address;
  return `${location.protocol}//${host}${location.port === '' ? '' : `:${location.port}`}/`;
}

/** Names, servers or domains typed into one field, split at commas and spaces. */
export function splitList(text: string): string[] {
  return text.split(/[\s,]+/).filter((entry) => entry !== '');
}

/** The configuration with one interface added or replaced. */
export function withInterface(config: NetworkConfig, item: InterfaceConfig): NetworkConfig {
  return {
    ...config,
    interfaces: [...config.interfaces.filter((other) => other.name !== item.name), item].sort(
      (a, b) => a.name.localeCompare(b.name),
    ),
  };
}

/**
 * The configuration without an interface: a card goes back to DHCP, a bond frees its cards, and VLANs on it and a
 * default route through it go with it.
 */
export function withoutInterface(config: NetworkConfig, name: string): NetworkConfig {
  return {
    interfaces: config.interfaces.filter(
      (item) => item.name !== name && item.vlan?.parent !== name,
    ),
    global: {
      ...config.global,
      defaultInterface:
        config.global.defaultInterface === name ? null : (config.global.defaultInterface ?? null),
    },
  };
}

/** Cards that may join the bond: cards without settings of their own, not in another bond and not carrying a VLAN. */
export function bondCandidates(
  links: NetworkLink[],
  config: NetworkConfig,
  bond: string,
): string[] {
  const configured = new Set(config.interfaces.map((item) => item.name));
  const otherMembers = new Set(
    config.interfaces
      .filter((item) => item.name !== bond)
      .flatMap((item) => item.bond?.members ?? []),
  );
  const parents = new Set(config.interfaces.flatMap((item) => item.vlan?.parent ?? []));
  return links
    .filter(
      (link) =>
        link.kind === 'ethernet' &&
        link.changeable &&
        !configured.has(link.name) &&
        !otherMembers.has(link.name) &&
        !parents.has(link.name),
    )
    .map((link) => link.name);
}

/** Interfaces that may carry a VLAN: cards outside a bond and bonds. */
export function vlanParents(links: NetworkLink[], config: NetworkConfig): string[] {
  const members = new Set(config.interfaces.flatMap((item) => item.bond?.members ?? []));
  const cards = links
    .filter((link) => link.kind === 'ethernet' && link.changeable && !members.has(link.name))
    .map((link) => link.name);
  const bonds = config.interfaces.filter((item) => item.kind === 'bond').map((item) => item.name);
  return [...new Set([...cards, ...bonds])].sort((a, b) => a.localeCompare(b));
}

/** The usual name of a VLAN, `<parent>.<id>`, or `vlan<id>` where that is longer than an interface name may be. */
export function vlanName(parent: string, id: number): string {
  const name = `${parent}.${String(id)}`;
  return name.length <= MAX_NAME_LENGTH ? name : `vlan${String(id)}`;
}

/** The next free bond name. */
export function bondName(config: NetworkConfig, links: NetworkLink[]): string {
  const taken = new Set([
    ...config.interfaces.map((item) => item.name),
    ...links.map((link) => link.name),
  ]);
  let index = 0;
  while (taken.has(`bond${String(index)}`)) {
    index += 1;
  }
  return `bond${String(index)}`;
}

/** Interfaces that can carry the default route: with settings, on, and with a gateway or DHCP. */
export function gatewayCandidates(config: NetworkConfig): string[] {
  return config.interfaces
    .filter(
      (item) =>
        item.enabled &&
        (item.ipv4 === 'dhcp' ||
          item.ipv6 === 'auto' ||
          (item.ipv4Gateway ?? null) !== null ||
          (item.ipv6Gateway ?? null) !== null),
    )
    .map((item) => item.name);
}

/** Compares two configurations by value, the way the daemon stores them. */
export function sameConfig(a: NetworkConfig, b: NetworkConfig): boolean {
  return JSON.stringify(normalize(a)) === JSON.stringify(normalize(b));
}

function normalize(config: NetworkConfig): unknown {
  return {
    interfaces: [...config.interfaces]
      .sort((a, b) => a.name.localeCompare(b.name))
      .map((item) => ({
        ...defaultInterface(item.name),
        ...item,
        bond:
          item.bond === undefined || item.bond === null
            ? null
            : {
                lacpRate: 'slow',
                primary: null,
                ...item.bond,
                members: [...item.bond.members].sort(),
              },
      })),
    global: {
      hostName: config.global.hostName,
      dnsServers: config.global.dnsServers,
      searchDomains: config.global.searchDomains,
      defaultInterface: config.global.defaultInterface ?? null,
    },
  };
}

/**
 * Where the box answers after the change, for a page reached at `host`: the static address the connection's card
 * gets in the same family, else the same host.
 */
export function hostAfterChange(links: NetworkLink[], config: NetworkConfig, host: string): string {
  const bare = host.replace(/^\[/, '').replace(/\]$/, '');
  const link = links.find((candidate) =>
    candidate.addresses.some((address) => address.split('/')[0] === bare),
  );
  const item = config.interfaces.find((candidate) => candidate.name === link?.name);
  if (item?.enabled !== true) {
    return host;
  }
  const ipv6 = bare.includes(':');
  const address = ipv6
    ? item.ipv6 === 'static'
      ? item.ipv6Address
      : null
    : item.ipv4 === 'static'
      ? item.ipv4Address
      : null;
  const plain = address?.split('/')[0];
  if (plain === undefined || plain === '') {
    return host;
  }
  return ipv6 ? `[${plain}]` : plain;
}
