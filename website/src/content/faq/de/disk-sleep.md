---
question: 'Weckt Pithyx meine schlafenden Platten auf?'
category: storage
tags: [spindown, standby, schlafen, lautstärke, stromverbrauch, hdd, aufwecken]
order: 60
status: ready
---

Pithyx vermeidet es: SMART-Werte liest es nur von wachen Platten, und der Ressourcenmonitor nutzt die Zähler des Kernels. Manche Aktionen wecken Platten trotzdem: ein Scrub, **Platten durchsuchen** beim Import und das optionale Log-Archiv etwa einmal am Tag. Eine Einstellung, die Platten schlafen legt, hat Pithyx nicht.

Details: [Speicher und Pools](/de/docs/admin/storage/).
