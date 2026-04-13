CREATE TABLE mesa_pedido (
  id INT NOT NULL PRIMARY KEY,
  descricao VARCHAR(200) NULL,
  valor DECIMAL(10,2) NULL,
  criado_em DATETIME NULL
);

CREATE TABLE a_caixa_delivery_pedido (
  id INT NOT NULL PRIMARY KEY,
  mesa_pedido_id INT NULL,
  status VARCHAR(50) NULL
);
