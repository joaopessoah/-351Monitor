-- 009: flag "não contactar" (opt-out) — lista de supressão.
-- A lead marcada nunca mais entra em cadência nem sai da fila de prospecção,
-- e o registro é mantido justamente para conseguir honrar o pedido.

ALTER TABLE leads
  ADD COLUMN no_contact TINYINT(1) NOT NULL DEFAULT 0,
  ADD COLUMN no_contact_at DATETIME NULL,
  ADD COLUMN no_contact_reason VARCHAR(255) NULL,
  ADD KEY idx_leads_no_contact (no_contact);
