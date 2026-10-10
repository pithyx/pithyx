---
question: 'Kann ich Pithyx in einer virtuellen Maschine betreiben?'
category: hardware
tags: [vm, virtuelle maschine, qemu, kvm, proxmox, hypervisor, virtualisierung]
order: 60
status: ready
---

Der Installer wird in virtuellen Maschinen mit QEMU, KVM und UEFI-Firmware getestet, das Image hängt dabei als USB-Stick an. Andere Hypervisoren, der Start als virtuelle CD und eine Bandbreite echter Hardware sind nicht getestet. Die VM braucht UEFI mit ausgeschaltetem Secure Boot und eigene virtuelle Platten für Pools.

Details: [Hardware-Anforderungen](/de/docs/getting-started/requirements/).
