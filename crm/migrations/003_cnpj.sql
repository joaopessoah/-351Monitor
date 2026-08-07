-- 003: CNPJ do lead + enriquecimento com dados públicos da Receita Federal.
-- cnpj: 14 posições normalizadas (alfanumérico suportado desde jul/2026).
-- cnpj_json: snapshot compacto da última consulta (razão, situação, CNAE, sócios...).

ALTER TABLE leads
  ADD COLUMN cnpj VARCHAR(14) NULL AFTER company,
  ADD COLUMN cnpj_razao_social VARCHAR(160) NULL AFTER cnpj,
  ADD COLUMN cnpj_situacao VARCHAR(40) NULL AFTER cnpj_razao_social,
  ADD COLUMN cnpj_json TEXT NULL AFTER cnpj_situacao,
  ADD COLUMN cnpj_checked_at DATETIME NULL AFTER cnpj_json,
  ADD KEY idx_leads_cnpj (cnpj);
