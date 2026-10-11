---
question: 'Which ports does Pithyx use?'
category: network
tags: [ports, firewall, '7484', '445', '22', '21', port forwarding, router]
order: 30
status: ready
---

The web desktop and WebDAV use port 7484 (plain HTTP), and port 80 redirects there. SMB uses 445, SFTP 22, and FTP 21 with passive data ports 50000 to 50099. The box's firewall lets in only the web ports and the file services that are switched on, and Settings > **File services** can limit the file services to private networks. Pithyx offers no remote access of its own; since the desktop is not encrypted yet, do not forward these ports from your router to the internet.

Details: [File services](/docs/admin/file-services/).
