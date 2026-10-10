---
question: 'Can I run Pithyx in a virtual machine?'
category: hardware
tags: [vm, virtual machine, qemu, kvm, proxmox, hypervisor, virtualization]
order: 60
status: ready
---

The installer is tested in virtual machines with QEMU and KVM and UEFI firmware, with the image attached as a USB stick. Other hypervisors, starting the image as a virtual CD and a range of real hardware have not been tested. The VM needs UEFI with Secure Boot off and its own virtual disks for pools.

Details: [Hardware requirements](/docs/getting-started/requirements/#community-platforms).
