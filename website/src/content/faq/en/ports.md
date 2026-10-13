---
question: 'Which ports does Pithyx use?'
category: network
tags: [ports, firewall, '7484', '445', '22', '21', port forwarding, router]
order: 30
status: ready
---

The web desktop and WebDAV use port 7484 (HTTPS); ports 80 and 443 only redirect there. SMB uses 445, SFTP 22, and FTP 21 with passive data ports 50000 to 50099. The box's firewall lets in only the web ports and the file services that are switched on, and Settings > **File services** can limit the file services to private networks. Pithyx offers no remote access of its own; reach the box from outside over a VPN rather than forwarding these ports from your router.

Details: [File services](/docs/admin/file-services/).
