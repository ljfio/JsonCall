---
type: Capability
title: "09 Broadcast"
description: "Push notifications to every connected peer, or a filtered subset."
tags: [capability, FR-BCAST, server]
feature: specs/features/09-broadcast.feature
---

# Requirements

- **FR-BCAST-01** A broadcast reaches every peer attached to a `JsonRpcConnectionSet`.
- **FR-BCAST-02** A peer that disconnects leaves the set.
- **FR-BCAST-03** A broadcast can be limited to the peers a filter selects.

# Related

[Transports guide](../guides/transports.md) (broadcasting) ·
[10 Generated client notifications](10-generated-client-notifications.md) for the receiving side
