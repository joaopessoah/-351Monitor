<?php
/**
 * Consulta de CNPJ em fontes públicas dos dados abertos da Receita Federal.
 * Sempre server-side (o site institucional continua sem chamadas externas
 * no navegador). Fontes gratuitas e sem chave, com fallback:
 *   1. BrasilAPI      https://brasilapi.com.br/api/cnpj/v1/{cnpj}
 *   2. minhareceita   https://minhareceita.org/{cnpj}
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

/** Exibição "12.ABC.345/0001-90" (suporta o CNPJ alfanumérico de 2026). */
function cnpj_format(?string $c): string
{
    if (!$c || strlen($c) !== 14) {
        return (string) $c;
    }
    return substr($c, 0, 2) . '.' . substr($c, 2, 3) . '.' . substr($c, 5, 3)
        . '/' . substr($c, 8, 4) . '-' . substr($c, 12, 2);
}

function cnpj_http_get_json(string $url): ?array
{
    $body = false;
    $code = 0;
    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_CONNECTTIMEOUT => 4,
            CURLOPT_TIMEOUT        => 8,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_MAXREDIRS      => 2,
            CURLOPT_HTTPHEADER     => ['Accept: application/json'],
            CURLOPT_USERAGENT      => 'M351-CRM/1.0 (+https://www.mais351monitor.com.br)',
        ]);
        $body = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);
    } else {
        $ctx = stream_context_create(['http' => [
            'timeout'       => 8,
            'ignore_errors' => true,
            'header'        => "Accept: application/json\r\nUser-Agent: M351-CRM/1.0\r\n",
        ]]);
        $body = @file_get_contents($url, false, $ctx);
        if (isset($http_response_header[0]) && preg_match('/\s(\d{3})\s/', $http_response_header[0], $m)) {
            $code = (int) $m[1];
        } elseif ($body !== false) {
            $code = 200;
        }
    }
    if ($body === false || $code >= 400 || $code === 0) {
        return null;
    }
    $d = json_decode((string) $body, true);
    return is_array($d) ? $d : null;
}

/**
 * Consulta um CNPJ normalizado (só dígitos/letras, 14 posições).
 * @return ?array shape fixo: razao_social, nome_fantasia, situacao, porte,
 *                cnae, municipio, uf, abertura, capital_social, telefone,
 *                email, socios[] — ou null se indisponível/não encontrado.
 */
function cnpj_lookup(string $cnpj): ?array
{
    $d = cnpj_http_get_json('https://brasilapi.com.br/api/cnpj/v1/' . rawurlencode($cnpj));
    if (!is_array($d) || empty($d['razao_social'])) {
        $d = cnpj_http_get_json('https://minhareceita.org/' . rawurlencode($cnpj));
    }
    if (!is_array($d) || empty($d['razao_social'])) {
        return null;
    }
    $socios = [];
    foreach ((array) ($d['qsa'] ?? []) as $s) {
        if (!empty($s['nome_socio'])) {
            $socios[] = (string) $s['nome_socio'];
        }
        if (count($socios) >= 5) {
            break;
        }
    }
    $cnaeNum = (string) ($d['cnae_fiscal'] ?? '');
    $cnaeDesc = (string) ($d['cnae_fiscal_descricao'] ?? '');
    return [
        'razao_social'   => (string) $d['razao_social'],
        'nome_fantasia'  => (string) ($d['nome_fantasia'] ?? ''),
        'situacao'       => (string) ($d['descricao_situacao_cadastral'] ?? $d['situacao_cadastral'] ?? ''),
        'porte'          => (string) ($d['porte'] ?? $d['descricao_porte'] ?? ''),
        'cnae'           => trim($cnaeNum . ' ' . $cnaeDesc),
        'municipio'      => (string) ($d['municipio'] ?? ''),
        'uf'             => (string) ($d['uf'] ?? ''),
        'abertura'       => (string) ($d['data_inicio_atividade'] ?? ''),
        'capital_social' => $d['capital_social'] ?? null,
        'telefone'       => (string) ($d['ddd_telefone_1'] ?? ''),
        'email'          => (string) ($d['email'] ?? ''),
        'socios'         => $socios,
    ];
}
