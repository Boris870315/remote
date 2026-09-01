---
status: accepted
---

# Permanently delete credentials and clear their references

Deleting a Credential permanently removes it and all of its retained versions from the active Vault instead of moving it to a recoverable trash area. Connections and Folders that referenced it are left with no Credential, never silently reassigned, and the Operator is shown the affected items after deletion; encrypted Vault Backups may still contain the deleted Credential and the confirmation must disclose that consequence.
