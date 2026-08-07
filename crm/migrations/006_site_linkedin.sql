-- 006: site e LinkedIn da empresa + LinkedIn por contato.
-- O site é derivado automaticamente do domínio do e-mail corporativo pelo
-- pipeline de prospecção; LinkedIn é preenchido manualmente na cadência.

ALTER TABLE leads
  ADD COLUMN website VARCHAR(190) NULL AFTER whatsapp,
  ADD COLUMN linkedin VARCHAR(190) NULL AFTER website;

ALTER TABLE lead_contacts ADD COLUMN linkedin VARCHAR(190) NULL AFTER whatsapp;

ALTER TABLE prospect_pool ADD COLUMN website VARCHAR(190) NULL AFTER whatsapp;
