-- 008: telefone fixo por contato (o WhatsApp continua em coluna própria,
-- porque só ele vira link wa.me).

ALTER TABLE lead_contacts ADD COLUMN phone VARCHAR(20) NULL AFTER whatsapp;
