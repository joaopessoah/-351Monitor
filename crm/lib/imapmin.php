<?php
/**
 * Cliente IMAP minimo, so o suficiente para observar a caixa de quem envia.
 *
 * Nao depende da extensao imap do PHP (que precisa ser ligada no hPanel e nem
 * sempre esta): IMAP e protocolo de texto sobre TLS, e as quatro operacoes que
 * usamos — LOGIN, EXAMINE, UID SEARCH e UID FETCH — cabem em pouca coisa.
 *
 * EXAMINE (e nao SELECT) + BODY.PEEK: a caixa e aberta em MODO LEITURA e
 * nenhuma mensagem e marcada como lida. O Outlook da Bruna continua exatamente
 * como estava; o CRM so olha.
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

final class ImapMin
{
    private int $tag = 0;

    /** @param resource $sock */
    private function __construct(private $sock)
    {
    }

    /** @throws RuntimeException */
    public static function abrir(string $host, int $porta, int $timeout = 20): self
    {
        $ctx = stream_context_create(['ssl' => [
            'verify_peer'      => true,
            'verify_peer_name' => true,
            'SNI_enabled'      => true,
            'peer_name'        => $host,
        ]]);
        $errNo = 0;
        $errStr = '';
        $sock = @stream_socket_client('ssl://' . $host . ':' . $porta, $errNo, $errStr,
            $timeout, STREAM_CLIENT_CONNECT, $ctx);
        if ($sock === false) {
            throw new RuntimeException('IMAP: nao conectou em ' . $host . ':' . $porta . ' — ' . $errStr);
        }
        stream_set_timeout($sock, $timeout);
        $self = new self($sock);
        $banner = fgets($sock, 8192);
        if ($banner === false || !str_starts_with($banner, '* OK')) {
            $self->fechar();
            throw new RuntimeException('IMAP: servidor nao respondeu com OK no banner.');
        }
        return $self;
    }

    /**
     * Manda um comando e le ate a resposta com a tag.
     * @return array{ok:bool, linhas:string[], literais:string[], erro:string}
     */
    public function comando(string $cmd): array
    {
        $tag = 'a' . str_pad((string) (++$this->tag), 4, '0', STR_PAD_LEFT);
        @fwrite($this->sock, $tag . ' ' . $cmd . "\r\n");
        $linhas = [];
        $literais = [];
        while (true) {
            $l = @fgets($this->sock, 16384);
            if ($l === false) {
                return ['ok' => false, 'linhas' => $linhas, 'literais' => $literais,
                    'erro' => 'conexao caiu ou expirou'];
            }
            // Literal no fim da linha ({1234}) = os proximos N bytes sao dados
            // crus, e a resposta continua na linha seguinte.
            while (preg_match('/\{(\d+)\}\r?\n$/', $l, $m)) {
                $n = (int) $m[1];
                $dados = '';
                while (strlen($dados) < $n) {
                    $pedaco = @fread($this->sock, min(16384, $n - strlen($dados)));
                    if ($pedaco === false || $pedaco === '') {
                        break 3; // socket morreu no meio do literal: aborta tudo
                    }
                    $dados .= $pedaco;
                }
                $literais[] = $dados;
                $l = @fgets($this->sock, 16384);
                if ($l === false) {
                    break 2;
                }
            }
            $linhas[] = $l;
            if (str_starts_with($l, $tag . ' ')) {
                return [
                    'ok'       => stripos($l, $tag . ' OK') === 0,
                    'linhas'   => $linhas,
                    'literais' => $literais,
                    'erro'     => trim(substr($l, strlen($tag) + 1)),
                ];
            }
        }
        return ['ok' => false, 'linhas' => $linhas, 'literais' => $literais, 'erro' => 'resposta truncada'];
    }

    /** Aspas do IMAP: \ e " precisam de escape. */
    private static function aspas(string $s): string
    {
        return '"' . str_replace(['\\', '"'], ['\\\\', '\\"'], $s) . '"';
    }

    public function login(string $usuario, string $senha): bool
    {
        $r = $this->comando('LOGIN ' . self::aspas($usuario) . ' ' . self::aspas($senha));
        return $r['ok'];
    }

    /**
     * Abre a caixa em modo leitura.
     * @return array{uidnext:int, uidvalidity:int}
     */
    public function examine(string $caixa = 'INBOX'): array
    {
        $r = $this->comando('EXAMINE ' . self::aspas($caixa));
        $out = ['uidnext' => 0, 'uidvalidity' => 0];
        foreach ($r['linhas'] as $l) {
            if (preg_match('/UIDNEXT (\d+)/i', $l, $m)) {
                $out['uidnext'] = (int) $m[1];
            }
            if (preg_match('/UIDVALIDITY (\d+)/i', $l, $m)) {
                $out['uidvalidity'] = (int) $m[1];
            }
        }
        return $out;
    }

    /**
     * UIDs maiores que $desde. O filtro em PHP e obrigatorio: o IMAP trata
     * "100:*" como "do maior ate 100" quando 100 ja passou do fim da caixa,
     * entao a busca sempre devolve pelo menos a ultima mensagem.
     * @return int[]
     */
    public function uidsDepoisDe(int $desde): array
    {
        $r = $this->comando('UID SEARCH UID ' . ($desde + 1) . ':*');
        $uids = [];
        foreach ($r['linhas'] as $l) {
            if (preg_match('/^\* SEARCH(.*)$/i', trim($l), $m)) {
                foreach (preg_split('/\s+/', trim($m[1])) as $n) {
                    if ($n !== '' && ctype_digit($n) && (int) $n > $desde) {
                        $uids[] = (int) $n;
                    }
                }
            }
        }
        sort($uids);
        return $uids;
    }

    /**
     * BODY.PEEK[$parte] de um UID. 'HEADER' ou 'TEXT'.
     *
     * Devolve null quando o FETCH FALHOU (timeout, conexao caida) e string
     * quando o servidor respondeu — inclusive '' para mensagem que sumiu entre
     * a busca e a leitura. A distincao existe porque quem chama nao pode
     * avancar o cursor de UID em cima de uma falha: seria perder a mensagem
     * para sempre, e ela pode ser justamente a resposta que estavamos esperando.
     */
    public function parte(int $uid, string $parte, ?int $bytes = null): ?string
    {
        $spec = 'BODY.PEEK[' . $parte . ']' . ($bytes !== null ? '<0.' . $bytes . '>' : '');
        $r = $this->comando('UID FETCH ' . $uid . ' (' . $spec . ')');
        if (!$r['ok']) {
            return null;
        }
        return $r['literais'][0] ?? '';
    }

    public function fechar(): void
    {
        if (is_resource($this->sock)) {
            @fwrite($this->sock, "z999 LOGOUT\r\n");
            @fclose($this->sock);
        }
    }
}
