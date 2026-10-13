---
question: 'How do I find the address of my box?'
category: install
tags: [ip address, address, headless, monitor, qr code, dhcp, router, cannot connect]
order: 30
status: ready
---

The console of the box shows its address, for example `https://192.168.1.50:7484` (`http://` while the installer runs, with a QR code). The box does not announce a name on the network, so without a screen look up its address in your router's list of DHCP clients. If the console says it is waiting for a network address, check the network cable.

Details: [Booting from the stick](/docs/install/firmware-and-boot/#booting-from-the-stick).
